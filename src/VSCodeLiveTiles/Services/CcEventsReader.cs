using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace VSCodeLiveTiles.Services;

/// <summary>CC フックイベント 1 件。events.jsonl の 1 行に対応する。</summary>
/// <param name="HasRunningBackgroundTasks">
/// stop 時点で走り続けている背景タスクがあるか（stop 以外では常に false）。
/// </param>
public sealed record CcEventRecord(
    long Ts, string Type, string? SessionId, string? ProjectName, string? Cwd, bool HasRunningBackgroundTasks);

/// <summary>
/// CCPet フック（append-event.mjs）が追記する events.jsonl の tail リーダー。
/// 起動時に末尾 512KB を遡って状態を再構築し、以後は FileSystemWatcher ＋
/// 1 秒間隔の保険ポーリング（WinEvent＋ポーリングと同じ思想）で追記分のみ読む。
///
/// - <b>ファイル操作とパースは必ずバックグラウンドスレッドで行う</b>。
///   events.jsonl は際限なく育つ（実測 42MB）。追記直後の open はファイル全体が
///   スキャンされるため、42MB で 140ms、ディスクが混んでいれば数秒かかる（実測）。
///   これを UI スレッドでやると Windows がウィジェットを「応答なし」と判定する（v0.8.3）
/// - このクラスはファイルに一切書き込まない（読み取り専用・FileShare.ReadWrite で開く）
/// - ファイル長がオフセットより短くなったらローテーションとみなし先頭から再読込
/// - 壊れた行・書き込み途中の行はスキップ（フック側も途中で切れた行はパース側でスキップ前提）
/// - 読み取った結果だけを UI スレッド（生成元 Dispatcher）へ渡して <see cref="Received"/> を発火する
/// </summary>
public sealed class CcEventsReader : IDisposable
{
    private const int TailBytes = 512 * 1024;
    private const int PollIntervalMs = 1000;
    private const int ScanChunkBytes = 1024 * 1024;
    private const int MaxCarryBytes = 4 * 1024 * 1024; // 改行が来ない異常行の保険

    /// <summary>session_start 行だけを拾うためのバイト列。JSON パース前にふるいにかける。</summary>
    private static readonly byte[] SessionStartMarker = Encoding.UTF8.GetBytes("\"session_start\"");

    private readonly string _file;
    private readonly Dispatcher _dispatcher;

    /// <summary>読み取りを 1 本に直列化する。取れなければ別スレッドが読んでいるので捨てる。</summary>
    private readonly object _gate = new();

    private FileSystemWatcher? _watcher;
    private Timer? _pollTimer;

    private long _offset;
    private byte[] _pendingBytes = Array.Empty<byte>(); // 改行未達の行末尾（マルチバイト分断対策でバイトのまま保持）
    private volatile bool _initialized;
    private volatile bool _disposed;

    /// <summary>新しいイベントを読み取ったときに UI スレッドで発火（追記順）。</summary>
    public event Action<IReadOnlyList<CcEventRecord>>? Received;

    public CcEventsReader()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _file = ResolveEventsFile();
    }

    private static string ResolveEventsFile()
    {
        var fromEnv = Environment.GetEnvironmentVariable("CCPET_EVENTS_FILE");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Path.IsPathRooted(fromEnv))
            return fromEnv;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ccpet", "events.jsonl");
    }

    /// <summary>
    /// 監視を開始する。フック未導入（ディレクトリ不在）なら false を返し、何もしない。
    /// ファイル自体の不在は「まだイベントがない」だけなので監視は続ける。
    /// 実際の読み取りはバックグラウンドで走るので、この呼び出しは即座に返る。
    /// </summary>
    public bool Start()
    {
        var dir = Path.GetDirectoryName(_file);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return false;

        try
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_file))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            _watcher.Changed += (_, _) => RequestDrain();
            _watcher.Created += (_, _) => RequestDrain();
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            _watcher = null; // ポーリングのみで続行
        }

        _pollTimer = new Timer(_ => Drain(), null, PollIntervalMs, PollIntervalMs);

        // 起動時の全体走査と初回 tail 読みは、42MB のファイルだと数百 ms かかる。
        // ウィンドウが出る前の UI スレッドを塞がないよう、初期化ごと逃がす
        ThreadPool.UnsafeQueueUserWorkItem(static self => self.Initialize(), this, preferLocal: false);
        return true;
    }

    /// <summary>
    /// 末尾 512KB の位置を求め、セッション開始時刻を全体走査で拾う。
    /// 状態は続く <see cref="Drain"/>（tail）が上書きするため、この順番でなければならない。
    /// </summary>
    private void Initialize()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            if (File.Exists(_file))
            {
                try
                {
                    using var fs = OpenShared();
                    if (fs.Length > TailBytes)
                    {
                        fs.Position = fs.Length - TailBytes;
                        SkipToNextLine(fs); // 行の途中（マルチバイトの途中の可能性あり）を読み捨てる
                    }
                    _offset = fs.Position;
                }
                catch
                {
                    _offset = 0;
                }
            }

            var starts = ScanSessionStarts();
            if (starts.Count > 0)
                Publish(starts);

            _initialized = true;
        }

        Drain();
    }

    /// <summary>
    /// ファイル全体を走査して session_start 行だけを返す（追記順）。
    /// 状態の再構築は末尾 512KB で足りるが、セッション開始時刻はそこに含まれないことが多い
    /// （実測 2026-07-09: 生存セッション 6 件中 5 件が範囲外）。
    /// JSON パースは該当行だけなので、数十 MB でも走査は一瞬で終わる。
    /// </summary>
    private List<CcEventRecord> ScanSessionStarts()
    {
        var found = new List<CcEventRecord>();
        try
        {
            using var fs = OpenShared();
            var buf = new byte[ScanChunkBytes];
            var carry = Array.Empty<byte>();

            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                var chunk = new byte[carry.Length + n];
                carry.CopyTo(chunk, 0);
                buf.AsSpan(0, n).CopyTo(chunk.AsSpan(carry.Length));

                int lineStart = 0;
                for (int i = 0; i < chunk.Length; i++)
                {
                    if (chunk[i] != '\n')
                        continue;
                    var line = chunk.AsSpan(lineStart, i - lineStart);
                    if (line.IndexOf(SessionStartMarker) >= 0)
                    {
                        var record = ParseLine(line);
                        if (record is not null && record.Type == "session_start")
                            found.Add(record);
                    }
                    lineStart = i + 1;
                }

                carry = lineStart >= chunk.Length ? Array.Empty<byte>() : chunk[lineStart..];
                if (carry.Length > MaxCarryBytes)
                    carry = Array.Empty<byte>(); // 壊れた行は捨てる（session_start 行は小さい）
            }
        }
        catch
        {
            // 走査できなくても状態追跡は続ける（開始時刻が出ないだけ）
        }
        return found;
    }

    private FileStream OpenShared()
        => new(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static void SkipToNextLine(FileStream fs)
    {
        int b;
        while ((b = fs.ReadByte()) != -1)
        {
            if (b == '\n')
                return;
        }
    }

    /// <summary>FileSystemWatcher の通知スレッドを塞がないよう、読み取りはワーカーへ投げる。</summary>
    private void RequestDrain()
    {
        if (_disposed)
            return;
        ThreadPool.UnsafeQueueUserWorkItem(static self => self.Drain(), this, preferLocal: false);
    }

    /// <summary>
    /// 追記分を読んでパースし、結果だけを UI スレッドへ渡す。バックグラウンドスレッドで動く。
    /// 既に別スレッドが読み取り中なら何もしない（取りこぼしは次のポーリングが拾う）。
    /// </summary>
    private void Drain()
    {
        if (_disposed || !_initialized)
            return;
        if (!Monitor.TryEnter(_gate))
            return;

        // 遅いときに「どの段階で」を切り分けられるよう、区間ごとに測る
        long started = Stopwatch.GetTimestamp();
        double msOpen = 0, msRead = 0, msParse = 0;
        int bytes = 0, records = 0;
        bool rotated = false;

        try
        {
            // ハンドルを開く前に長さだけ確認し、未変化ならポーリングを空振りで終える
            var fi = new FileInfo(_file);
            if (!fi.Exists || fi.Length == _offset)
                return;

            long t = Stopwatch.GetTimestamp();
            using var fs = OpenShared();
            msOpen = Elapsed(t);

            if (fs.Length < _offset)
            {
                // ローテーション（truncate / 差し替え）: 先頭から読み直す
                _offset = 0;
                _pendingBytes = Array.Empty<byte>();
                rotated = true;
            }
            if (fs.Length == _offset)
                return;

            t = Stopwatch.GetTimestamp();
            fs.Position = _offset;
            var appended = new byte[fs.Length - _offset];
            int read = 0;
            while (read < appended.Length)
            {
                int n = fs.Read(appended, read, appended.Length - read);
                if (n <= 0)
                    break;
                read += n;
            }
            _offset += read;
            bytes = read;
            msRead = Elapsed(t);

            t = Stopwatch.GetTimestamp();
            var parsed = ParseLines(appended.AsSpan(0, read));
            records = parsed.Count;
            msParse = Elapsed(t);

            if (parsed.Count > 0)
                Publish(parsed);
        }
        catch
        {
            // 読めない瞬間（書き込み競合等）は次のポーリングで回収する
        }
        finally
        {
            Monitor.Exit(_gate);

            double total = Elapsed(started);
            if (total >= Log.SlowMs)
            {
                Log.Warn($"events.jsonl の読み取り {total:F0} ms（背景スレッド）" +
                    $"（open {msOpen:F0} / read {msRead:F0} [{bytes:N0}B{(rotated ? " 全読み直し" : "")}]" +
                    $" / parse {msParse:F0} [{records}件]）");
            }
        }
    }

    /// <summary>読み取り結果を UI スレッドへ渡す。呼び出し順（＝追記順）は Dispatcher が保つ。</summary>
    private void Publish(IReadOnlyList<CcEventRecord> records)
    {
        try
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed)
                    Received?.Invoke(records);
            }));
        }
        catch
        {
            // Dispatcher が終了処理に入っている
        }
    }

    private static double Elapsed(long since) => Stopwatch.GetElapsedTime(since).TotalMilliseconds;

    /// <summary>
    /// 追記分を前回の未完行と連結し、完全な行だけをパースする。
    /// 最後の改行以降（書き込み途中の行）はバイトのまま持ち越す。
    /// </summary>
    private List<CcEventRecord> ParseLines(ReadOnlySpan<byte> appended)
    {
        var buf = new byte[_pendingBytes.Length + appended.Length];
        _pendingBytes.CopyTo(buf, 0);
        appended.CopyTo(buf.AsSpan(_pendingBytes.Length));

        var records = new List<CcEventRecord>();
        int lineStart = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            if (buf[i] != '\n')
                continue;
            var record = ParseLine(buf.AsSpan(lineStart, i - lineStart));
            if (record is not null)
                records.Add(record);
            lineStart = i + 1;
        }
        _pendingBytes = lineStart >= buf.Length ? Array.Empty<byte>() : buf[lineStart..];
        return records;
    }

    private static CcEventRecord? ParseLine(ReadOnlySpan<byte> line)
    {
        // CR（CRLF 対応）と空行を除去
        if (line.Length > 0 && line[^1] == '\r')
            line = line[..^1];
        if (line.IsEmpty)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(line));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return null;

            long ts = root.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number
                ? tsEl.GetInt64() : 0;
            return new CcEventRecord(
                ts,
                typeEl.GetString()!,
                GetStringOrNull(root, "sessionId"),
                GetStringOrNull(root, "projectName"),
                GetStringOrNull(root, "cwd"),
                HasRunningBackgroundTask(root));
        }
        catch
        {
            return null; // 壊れた行はスキップ
        }
    }

    private static string? GetStringOrNull(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>
    /// payload.background_tasks に status="running" のタスクがあるか。
    /// メインエージェントが喋り終わって stop が出ても、サブエージェント（type=subagent）や
    /// run_in_background の Bash（type=shell）はそのまま走り続ける。
    /// 終わったタスクはリストから消えるので、running が 1 つでもあれば作業は継続中。
    /// </summary>
    private static bool HasRunningBackgroundTask(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return false;
        if (!payload.TryGetProperty("background_tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var task in tasks.EnumerateArray())
        {
            if (task.ValueKind == JsonValueKind.Object
                && task.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && status.ValueEquals("running"))
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pollTimer?.Dispose();
        _watcher?.Dispose();
    }
}
