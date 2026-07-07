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
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // ShowWindow コマンド
    public const int SW_RESTORE = 9;
    public const int SW_MAXIMIZE = 3;
    public const int SW_SHOW = 5;

    // SetWindowPos フラグ
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
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
    /// 対象ウィンドウを指定モニター矩形（作業領域）へ移動して最大化・最前面化する。
    /// フォアグラウンド制限を AttachThreadInput で回避する。
    /// </summary>
    public static void MoveMaximizeAndFocus(IntPtr hWnd, int monitorX, int monitorY, int monitorWidth, int monitorHeight)
    {
        if (hWnd == IntPtr.Zero)
            return;

        // 最大化状態だと SetWindowPos で別モニターへ移動できないので一旦 Restore
        if (IsIconic(hWnd) || IsZoomed(hWnd))
            ShowWindow(hWnd, SW_RESTORE);

        // 対象モニターの左上へ移動（サイズは仮。直後に最大化するので概算でよい）
        SetWindowPos(hWnd, HWND_TOP, monitorX, monitorY,
            Math.Max(400, monitorWidth / 2), Math.Max(300, monitorHeight / 2),
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // そのモニター上で最大化
        ShowWindow(hWnd, SW_MAXIMIZE);

        ForceForeground(hWnd);
    }

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    private static void ForceForeground(IntPtr hWnd)
    {
        uint foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint thisThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(hWnd, out _);

        bool attached1 = false, attached2 = false;
        try
        {
            if (foreThread != thisThread)
                attached1 = AttachThreadInput(thisThread, foreThread, true);
            if (targetThread != thisThread && targetThread != foreThread)
                attached2 = AttachThreadInput(thisThread, targetThread, true);

            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached1)
                AttachThreadInput(thisThread, foreThread, false);
            if (attached2)
                AttachThreadInput(thisThread, targetThread, false);
        }
    }
}
