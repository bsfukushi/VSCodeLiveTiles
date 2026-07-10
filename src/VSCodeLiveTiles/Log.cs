using System.Diagnostics;
using System.IO;
using System.Text;

namespace VSCodeLiveTiles;

/// <summary>
/// %LOCALAPPDATA%\VSCodeLiveTiles\logs\yyyy-MM-dd.log に追記するだけの最小ロガー。
///
/// 常駐ウィジェットなので「ログのせいで落ちる・肥大化する」ことを何より避ける:
/// - 書き込みに失敗しても例外を外へ出さない（記録できないことはアプリを止める理由にならない）
/// - 日付ごとに 1 ファイル。起動時に <see cref="RetentionDays"/> より古いものを削除する
///
/// 記録するのは例外と、原因調査に効く事象（起動時の環境サマリー・UI スレッドの停止・
/// 相手プロセス待ちで遅延した Win32 呼び出し）だけに絞る。常時の動作ログは書かない。
/// </summary>
public static class Log
{
    private const int RetentionDays = 7;

    /// <summary>UI スレッド上の処理がこれ以上かかったら「遅い」とみなす既定値。</summary>
    public const int SlowMs = 200;

    private static readonly object Gate = new();
    private static string? _dir;

    /// <summary>ログの出力先ディレクトリ（初期化前・初期化失敗時は null）。</summary>
    public static string? Directory => _dir;

    /// <summary>出力先を用意して古いログを掃除する。失敗した場合はログ機能ごと無効になる。</summary>
    public static void Start()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VSCodeLiveTiles", "logs");
            System.IO.Directory.CreateDirectory(dir);
            _dir = dir;
            PruneOldFiles(dir);
        }
        catch
        {
            _dir = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    /// <summary>
    /// UI スレッド上の処理が閾値より長くかかったときだけ記録する。
    /// 呼び出し側は <see cref="Stopwatch.GetTimestamp"/> の値を渡す（Stopwatch の確保を避けるため。
    /// レイアウト追従のように毎フレーム走る場所からも呼ぶ）。
    /// </summary>
    public static void SlowIf(string operation, long startedAt, int thresholdMs)
    {
        double ms = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        if (ms >= thresholdMs)
            Warn($"{operation} に {ms:F0} ms かかりました");
    }

    private static void Write(string level, string message)
    {
        var dir = _dir;
        if (dir is null)
            return;

        try
        {
            var now = DateTime.Now;
            var line = new StringBuilder()
                .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .AppendLine(message)
                .ToString();

            // ファイル名を書き込み時に決めるので、日付をまたいでも自然に次のファイルへ移る
            var path = Path.Combine(dir, $"{now:yyyy-MM-dd}.log");
            lock (Gate)
                File.AppendAllText(path, line, Encoding.UTF8);
        }
        catch
        {
            // 記録できないこと自体は握りつぶす（ログのために常駐を止めない）
        }
    }

    private static void PruneOldFiles(string dir)
    {
        var limit = DateTime.Now.Date.AddDays(-RetentionDays);
        foreach (var path in System.IO.Directory.EnumerateFiles(dir, "*.log"))
        {
            try
            {
                if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd",
                        null, System.Globalization.DateTimeStyles.None, out var day)
                    && day < limit)
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 消せないファイルは次の起動でまた試す
            }
        }
    }
}
