using System.Runtime.InteropServices;

namespace VSCodeLiveTiles.Services;

/// <summary>
/// 物理ピクセル基準のモニター情報を列挙する。EnumDisplayMonitors + GetMonitorInfo を使用。
/// WPF の論理座標ではなく物理座標を扱う（DWM サムネイル矩形・SetWindowPos が物理座標のため）。
/// </summary>
public sealed class MonitorService
{
    public sealed record MonitorInfoEx(
        int Index, bool IsPrimary,
        int Left, int Top, int Width, int Height,          // モニター全体（物理px）
        int WorkLeft, int WorkTop, int WorkWidth, int WorkHeight); // 作業領域（タスクバー除く）

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprc, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    public IReadOnlyList<MonitorInfoEx> GetMonitors()
    {
        var list = new List<MonitorInfoEx>();
        int idx = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                bool primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                list.Add(new MonitorInfoEx(
                    idx++, primary,
                    mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                    mi.rcWork.Left, mi.rcWork.Top,
                    mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>ウィジェットを載せるモニター。config 指定 → 最初の非プライマリ → プライマリ の順。</summary>
    public MonitorInfoEx GetWidgetMonitor(int? configIndex)
    {
        var mons = GetMonitors();
        if (mons.Count == 0)
            throw new InvalidOperationException("モニターが検出できませんでした。");

        if (configIndex is int i && i >= 0 && i < mons.Count)
            return mons[i];

        var nonPrimary = mons.FirstOrDefault(m => !m.IsPrimary);
        return nonPrimary ?? mons.First(m => m.IsPrimary);
    }

    /// <summary>クリックで最大化する先。config 指定 → プライマリ → 先頭 の順。</summary>
    public MonitorInfoEx GetTargetMonitor(int? configIndex)
    {
        var mons = GetMonitors();
        if (mons.Count == 0)
            throw new InvalidOperationException("モニターが検出できませんでした。");

        if (configIndex is int i && i >= 0 && i < mons.Count)
            return mons[i];

        return mons.FirstOrDefault(m => m.IsPrimary) ?? mons[0];
    }
}
