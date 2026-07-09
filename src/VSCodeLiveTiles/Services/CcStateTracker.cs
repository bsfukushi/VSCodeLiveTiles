using System.IO;

namespace VSCodeLiveTiles.Services;

/// <summary>
/// Claude Code セッションの表示状態。値の大きい方が優先（ウィンドウ単位の代表選出に使う）。
///
/// 同じフォルダ名に複数セッションがぶら下がるとき（同一プロジェクトを別ウィンドウで開いた等）、
/// Done は Working より弱い。完了は過去の結果、作業中は現在進行なので、
/// 片方が動いているならタイルは「作業中」を出すべき。
/// </summary>
public enum CcState
{
    None = 0,
    Done = 1,
    Working = 2,
    WaitingPermission = 3,
    WaitingQuestion = 4,
}

/// <summary>
/// events.jsonl のイベント列から sessionId ごとの状態を保持し、
/// タイルのキャプションと cwd/projectName を照合してウィンドウ単位の代表状態を返す。
///
/// 状態機械は「同一セッションの最新イベントが状態を上書きする」だけの単純規則
/// （SPEC §3）。イベント欠落があっても次のイベントで自然回復する。
/// </summary>
public sealed class CcStateTracker
{
    /// <summary>クラッシュ等で session_end が来ないセッションの掃除しきい値。</summary>
    private static readonly TimeSpan StaleAge = TimeSpan.FromHours(24);

    private sealed class Session
    {
        public CcState State;
        public long SinceTs;   // 現在の状態に遷移したイベントの ts（経過時間表示の基準）
        public long LastTs;    // 最終イベントの ts（鮮度切れ判定）
        public long StartTs;   // session_start の ts（0 = 不明。ローテーションで流れた場合）
        public string? FolderName;
        public string? ProjectName;
    }

    private readonly Dictionary<string, Session> _sessions = new();

    /// <summary>いずれかのセッション状態が変化したときに発火。</summary>
    public event Action? Changed;

    public void Apply(IReadOnlyList<CcEventRecord> records)
    {
        bool changed = false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var r in records)
        {
            if (r.SessionId is null)
                continue;

            // ts の健全性確認: 欠落(0)・壊れた値（未来すぎ/負）は現在時刻に置換する。
            // 不正値をそのまま持つと Resolve の FromUnixTimeMilliseconds が範囲外例外で
            // UI スレッドを落とし、経過時間表示も破綻するため入口で塞ぐ。
            long ts = r.Ts > 0 && r.Ts <= now + 3_600_000 ? r.Ts : now;

            if (r.Type == "session_end")
            {
                changed |= _sessions.Remove(r.SessionId);
                continue;
            }

            var state = MapState(r);
            if (state is null)
            {
                // 未知イベントは状態を変えないが、鮮度だけは更新する（前方互換・誤 purge 防止）
                if (_sessions.TryGetValue(r.SessionId, out var known))
                    known.LastTs = ts;
                continue;
            }

            if (!_sessions.TryGetValue(r.SessionId, out var s))
            {
                s = new Session { State = state.Value, SinceTs = ts };
                _sessions[r.SessionId] = s;
                changed = true;
            }
            else if (s.State != state.Value)
            {
                s.State = state.Value;
                s.SinceTs = ts;
                changed = true;
            }

            s.LastTs = ts;

            // 開始時刻は最初に観測した session_start を採用する。
            // resume では session_start が再発火するが、そこで上書きすると
            // 「そのウィンドウで作業を始めた時刻」より新しくなってしまう
            if (r.Type == "session_start" && s.StartTs == 0)
            {
                s.StartTs = ts;
                changed = true;
            }

            if (r.Cwd is not null)
                s.FolderName = SafeBaseName(r.Cwd);
            if (r.ProjectName is not null)
                s.ProjectName = r.ProjectName;
        }

        changed |= PurgeStale();

        if (changed)
            Changed?.Invoke();
    }

    private static CcState? MapState(CcEventRecord r) => r.Type switch
    {
        "pre_tool_use" => CcState.WaitingQuestion,        // フック側で AskUserQuestion のみに絞られている
        "permission_request" => CcState.WaitingPermission,
        "notification" => CcState.WaitingPermission,      // 許可要求・アイドル通知も「承認待ち」扱い（SPEC §2）
        // stop はメインエージェントのターン終了にすぎない。サブエージェントや
        // run_in_background の Bash が残っていれば作業は続いている（SPEC §8）
        "stop" => r.HasRunningBackgroundTasks ? CcState.Working : CcState.Done,
        "user_prompt_submit" => CcState.Working,
        "post_tool_use" => CcState.Working,               // AUQ 回答後・承認後の解除もこれで拾える
        "session_start" => CcState.None,
        _ => null,
    };

    private bool PurgeStale()
    {
        long threshold = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)StaleAge.TotalMilliseconds;
        var stale = _sessions.Where(kv => kv.Value.LastTs < threshold).Select(kv => kv.Key).ToList();
        foreach (var key in stale)
            _sessions.Remove(key);
        return stale.Count > 0;
    }

    /// <summary>
    /// タイルのキャプション（サフィックス除去後）に対応する代表状態を返す。
    /// 照合はキャプションが cwd の basename（補助: projectName）で終わるか（SPEC §4）。
    /// マッチなし・None のみなら null（バッジなし）。
    /// Started は代表セッションの開始時刻（session_start を観測できなければ null）。
    /// </summary>
    public (CcState State, DateTime Since, DateTime? Started)? Resolve(string strippedCaption)
    {
        Session? best = null;
        foreach (var s in _sessions.Values)
        {
            if (s.State == CcState.None)
                continue;
            if (!Matches(strippedCaption, s))
                continue;
            if (best is null || s.State > best.State || (s.State == best.State && s.SinceTs > best.SinceTs))
                best = s;
        }

        if (best is null)
            return null;
        return (
            best.State,
            DateTimeOffset.FromUnixTimeMilliseconds(best.SinceTs).LocalDateTime,
            best.StartTs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(best.StartTs).LocalDateTime : null);
    }

    private static bool Matches(string caption, Session s)
        => EndsWithName(caption, s.FolderName) || EndsWithName(caption, s.ProjectName);

    private static bool EndsWithName(string caption, string? name)
        => !string.IsNullOrEmpty(name) && caption.EndsWith(name, StringComparison.OrdinalIgnoreCase);

    private static string? SafeBaseName(string cwd)
    {
        try
        {
            var name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }
}
