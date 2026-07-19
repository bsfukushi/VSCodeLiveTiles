using System.Runtime.InteropServices;

namespace VSCodeLiveTiles.Interop;

/// <summary>
/// DWM Thumbnail API の薄いラッパ。
/// タスクバーのホバープレビューと同じ仕組みで、任意ウィンドウのライブ縮小表示を
/// 別ウィンドウ（登録先 HWND）内の矩形へ GPU 合成する。
/// </summary>
internal static class DwmThumbnail
{
    private const int DWM_TNP_RECTDESTINATION = 0x00000001;
    private const int DWM_TNP_RECTSOURCE = 0x00000002;
    private const int DWM_TNP_OPACITY = 0x00000004;
    private const int DWM_TNP_VISIBLE = 0x00000008;
    private const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        public bool fVisible;
        public bool fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumbId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbId, ref DWM_THUMBNAIL_PROPERTIES props);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbId, out SIZE size);

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx, cy;
    }

    /// <summary>
    /// source ウィンドウを dest ウィンドウのサムネイルとして登録する。
    /// 失敗時は IntPtr.Zero を返す。
    /// </summary>
    public static IntPtr Register(IntPtr dest, IntPtr source)
    {
        if (dest == IntPtr.Zero || source == IntPtr.Zero)
            return IntPtr.Zero;
        return DwmRegisterThumbnail(dest, source, out var id) == 0 ? id : IntPtr.Zero;
    }

    public static void Unregister(IntPtr thumbId)
    {
        if (thumbId != IntPtr.Zero)
            DwmUnregisterThumbnail(thumbId);
    }

    /// <summary>
    /// サムネイルの表示先矩形（登録先ウィンドウのクライアント座標・物理ピクセル）を更新する。
    /// アスペクト比を保ったまま矩形内にレターボックス配置する。
    /// opacity はサムネイル画像自体の不透明度（WPF の Opacity は DWM 合成には効かないため、
    /// ドラッグ中の半透明化はここで指定する）。
    /// </summary>
    public static void UpdateDestination(IntPtr thumbId, int x, int y, int width, int height,
        bool visible = true, byte opacity = 255)
    {
        if (thumbId == IntPtr.Zero || width <= 0 || height <= 0)
            return;

        // ソースのアスペクト比を取得してレターボックス化（引き伸ばし防止）
        var rect = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
        if (DwmQueryThumbnailSourceSize(thumbId, out var srcSize) == 0 && srcSize.cx > 0 && srcSize.cy > 0)
        {
            double scale = Math.Min((double)width / srcSize.cx, (double)height / srcSize.cy);
            int w = Math.Max(1, (int)(srcSize.cx * scale));
            int h = Math.Max(1, (int)(srcSize.cy * scale));
            int ox = x + (width - w) / 2;
            int oy = y + (height - h) / 2;
            rect = new RECT { Left = ox, Top = oy, Right = ox + w, Bottom = oy + h };
        }

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = rect,
            opacity = opacity,
            fVisible = visible,
            fSourceClientAreaOnly = false,
        };
        DwmUpdateThumbnailProperties(thumbId, ref props);
    }

    public static void SetVisible(IntPtr thumbId, bool visible)
    {
        if (thumbId == IntPtr.Zero)
            return;
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_VISIBLE,
            fVisible = visible,
        };
        DwmUpdateThumbnailProperties(thumbId, ref props);
    }
}
