using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VSCodeLiveTiles.Interop;

/// <summary>
/// user32 / kernel32 のウィンドウ列挙・操作ラッパ。
/// </summary>
public static class NativeWindows
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>現在の前面ウィンドウのハンドル。</summary>
    public static IntPtr GetForeground() => GetForegroundWindow();

    // ShowWindow コマンド
    public const int SW_RESTORE = 9;
    public const int SW_MAXIMIZE = 3;
    public const int SW_SHOW = 5;

    // SetWindowPos フラグ
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;

    // GetWindowLong インデックス / スタイル
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;

    public sealed record WindowInfo(IntPtr Handle, string Title, uint ProcessId, bool IsMinimized);

    /// <summary>
    /// 指定プロセス名（拡張子なし、大文字小文字無視）のトップレベル・可視・タイトル有りの
    /// ウィンドウを列挙する。excludeHandle（自ウィンドウ）は除外。
    /// </summary>
    public static List<WindowInfo> EnumerateWindows(IReadOnlySet<string> processNames, IntPtr excludeHandle)
    {
        var result = new List<WindowInfo>();
        var pidToName = new Dictionary<uint, string?>();
        var shell = GetShellWindow();

        EnumWindows((hWnd, _) =>
        {
            if (hWnd == excludeHandle || hWnd == shell)
                return true;
            if (!IsWindowVisible(hWnd))
                return true;

            // ツールウィンドウ（タスクバーに出ない補助窓）を除外
            long exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
                return true;

            int len = GetWindowTextLength(hWnd);
            if (len == 0)
                return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0)
                return true;

            if (!pidToName.TryGetValue(pid, out var procName))
            {
                procName = TryGetProcessName(pid);
                pidToName[pid] = procName;
            }
            if (procName is null || !processNames.Contains(procName))
                return true;

            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title))
                return true;

            result.Add(new WindowInfo(hWnd, title, pid, IsIconic(hWnd)));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static string? TryGetProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName; // 拡張子なし（例: "Code"）
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 対象ウィンドウを指定モニター矩形（作業領域）で最大化・最前面化する。
    /// すでに対象モニターに居る場合は移動をスキップし、最大化済みなら前面化だけにする
    /// （不要な 復元→移動→最大化 を挟むと画面がズレながら再描画されて見える）。
    /// 復元・移動・最大化はすべて非同期（相手のキューに積むだけ）で行う。
    /// キューは順番どおり処理されるので 復元→移動→最大化 の順序は保たれる。
    ///
    /// ただし末尾の <see cref="SetForegroundWindow"/> だけは相手スレッドの
    /// アクティベーション処理を待つ同期呼び出しで、相手が固まっていると Windows の
    /// ハングアプリ判定（5 秒）まで戻ってこない。
    /// <b>UI スレッドから呼んではならない</b>（呼ぶとウィジェット自身が「応答なし」の
    /// ゴーストウィンドウに置き換わる。v0.8.1）。
    /// </summary>
    public static void MoveMaximizeAndFocus(IntPtr hWnd, int monitorX, int monitorY, int monitorWidth, int monitorHeight)
    {
        if (hWnd == IntPtr.Zero)
            return;

        var targetMonitor = MonitorFromPoint(
            new POINT { X = monitorX + monitorWidth / 2, Y = monitorY + monitorHeight / 2 },
            MONITOR_DEFAULTTONEAREST);

        // 最小化中は現在座標が画面外（-32000）なので、復元先（通常時の矩形）でモニターを判定する
        bool minimized = IsIconic(hWnd);
        var currentMonitor = minimized
            ? GetRestoreMonitor(hWnd)
            : MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);

        if (currentMonitor == targetMonitor)
        {
            // 対象モニターに居る → 移動不要。最大化済みなら前面化だけ（タスクバー切替と同じ見え方）
            if (minimized || !IsZoomed(hWnd))
                ShowWindowAsync(hWnd, SW_MAXIMIZE);
        }
        else
        {
            // 別モニターに居る → 最大化状態だと SetWindowPos で移動できないので一旦 Restore
            if (minimized || IsZoomed(hWnd))
                ShowWindowAsync(hWnd, SW_RESTORE);

            // 対象モニターの左上へ移動（サイズは仮。直後に最大化するので概算でよい）
            SetWindowPos(hWnd, HWND_TOP, monitorX, monitorY,
                Math.Max(400, monitorWidth / 2), Math.Max(300, monitorHeight / 2),
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_ASYNCWINDOWPOS);

            // そのモニター上で最大化
            ShowWindowAsync(hWnd, SW_MAXIMIZE);
        }

        // クリック直後はこのプロセスがフォアグラウンドなので、素の SetForegroundWindow が通る
        // （AttachThreadInput は相手スレッドが詰まると自分の入力処理まで停止するため使わない）。
        // 待たされた事実はログに残す — 相手が固まっていたことの唯一の証拠になる
        var sw = Stopwatch.StartNew();
        SetForegroundWindow(hWnd);
        sw.Stop();
        if (sw.ElapsedMilliseconds >= SlowForegroundMs)
            Log.Warn($"SetForegroundWindow が {sw.ElapsedMilliseconds} ms 待たされました（hwnd=0x{hWnd:X}・相手プロセスが応答していません）");
    }

    /// <summary>この時間以上 SetForegroundWindow が返らなければ、相手が詰まっているとみなして記録する。</summary>
    private const int SlowForegroundMs = 200;

    /// <summary>最小化中のウィンドウが復元されるはずのモニター（通常時矩形の中心で判定）。</summary>
    private static IntPtr GetRestoreMonitor(IntPtr hWnd)
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hWnd, ref wp))
            return IntPtr.Zero;
        var center = new POINT
        {
            X = (wp.rcNormalPosition.Left + wp.rcNormalPosition.Right) / 2,
            Y = (wp.rcNormalPosition.Top + wp.rcNormalPosition.Bottom) / 2,
        };
        return MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
    }

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public uint flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
}
