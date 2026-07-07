using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using VSCodeLiveTiles.Controls;
using VSCodeLiveTiles.Interop;
using VSCodeLiveTiles.Services;

namespace VSCodeLiveTiles;

/// <summary>
/// サブモニターに全画面ボーダーレスで常駐し、対象ウィンドウをライブサムネイルの
/// 自動グリッドで並べるホストウィンドウ。
///
/// DWM サムネイルはトップレベルウィンドウ（このウィンドウ本体の HWND）を登録先にして
/// 初めて合成される。各タイルの矩形をこのウィンドウのクライアント座標（物理px）で計算し、
/// レイアウト変化のたびに DWM の表示先を追従させる。
/// </summary>
public sealed class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly MonitorService _monitors;
    private readonly UniformGrid _grid;
    private readonly Grid _root;
    private readonly TextBlock _emptyLabel;

    private WindowTracker? _tracker;
    private IntPtr _selfHandle;
    private IntPtr _activeHandle;

    // handle → タイル。thumbs: source handle → DWM サムネイル ID（最小化中は登録しない）
    private readonly Dictionary<IntPtr, ThumbnailTile> _tiles = new();
    private readonly Dictionary<IntPtr, IntPtr> _thumbs = new();
    private readonly List<IntPtr> _order = new();

    public MainWindow(AppConfig config, MonitorService monitors)
    {
        _config = config;
        _monitors = monitors;

        Title = "VSCode Live Tiles";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x11, 0x10));

        _grid = new UniformGrid { Margin = new Thickness(4) };

        _emptyLabel = new TextBlock
        {
            Text = "VSCode ウィンドウがありません",
            Foreground = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x78)),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        _root = new Grid();
        _root.Children.Add(_grid);
        _root.Children.Add(_emptyLabel);
        Content = _root;

        // レイアウトが変わるたびにサムネイル矩形を追従させる
        _root.LayoutUpdated += (_, _) => UpdateThumbnailRects();
        SizeChanged += (_, _) => UpdateThumbnailRects();
        DpiChanged += (_, _) => UpdateThumbnailRects();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _selfHandle = new WindowInteropHelper(this).Handle;

        PositionOnWidgetMonitor();

        _activeHandle = NativeWindows.GetForeground();

        _tracker = new WindowTracker(_config, () => _selfHandle);
        _tracker.Updated += OnWindowsUpdated;
        _tracker.ActiveWindowChanged += OnActiveWindowChanged;
        _tracker.Start();
    }

    private void OnActiveWindowChanged(IntPtr handle)
    {
        _activeHandle = handle;
        ApplyActiveHighlight();
    }

    private void ApplyActiveHighlight()
    {
        foreach (var (handle, tile) in _tiles)
            tile.SetActive(handle == _activeHandle);
    }

    /// <summary>ウィジェット用モニターの作業領域へ物理ピクセルで配置する（DPI 変換を避ける）。</summary>
    private void PositionOnWidgetMonitor()
    {
        var m = _monitors.GetWidgetMonitor(_config.WidgetMonitorIndex);
        NativeWindows.SetWindowPos(_selfHandle, NativeWindows.HWND_TOP,
            m.WorkLeft, m.WorkTop, m.WorkWidth, m.WorkHeight,
            NativeWindows.SWP_NOZORDER | NativeWindows.SWP_NOACTIVATE | NativeWindows.SWP_SHOWWINDOW);
    }

    private void OnWindowsUpdated(IReadOnlyList<NativeWindows.WindowInfo> windows)
    {
        var desired = windows.OrderBy(w => (long)w.Handle).ToList();
        var desiredHandles = new HashSet<IntPtr>(desired.Select(w => w.Handle));

        // 消えたウィンドウ：サムネイル解放 + タイル除去
        foreach (var handle in _order.Where(h => !desiredHandles.Contains(h)).ToList())
        {
            UnregisterThumb(handle);
            if (_tiles.TryGetValue(handle, out var tile))
            {
                _grid.Children.Remove(tile);
                _tiles.Remove(handle);
            }
        }

        // 追加・更新
        for (int i = 0; i < desired.Count; i++)
        {
            var info = desired[i];
            if (_tiles.TryGetValue(info.Handle, out var tile))
            {
                if (tile.IsMinimizedState != info.IsMinimized)
                    tile.SetMinimized(info.IsMinimized);
                tile.UpdateTitle(info);
            }
            else
            {
                tile = new ThumbnailTile(_config);
                tile.Clicked += OnTileClicked;
                tile.Bind(info);
                _tiles[info.Handle] = tile;
                _grid.Children.Insert(i, tile);
            }

            // サムネイルは「表示中(非最小化)」のときだけ登録する
            bool wantThumb = !info.IsMinimized;
            bool hasThumb = _thumbs.ContainsKey(info.Handle);
            if (wantThumb && !hasThumb)
            {
                var id = DwmThumbnail.Register(_selfHandle, info.Handle);
                if (id != IntPtr.Zero)
                    _thumbs[info.Handle] = id;
            }
            else if (!wantThumb && hasThumb)
            {
                UnregisterThumb(info.Handle);
            }
        }

        _order.Clear();
        _order.AddRange(desired.Select(w => w.Handle));

        int n = desired.Count;
        _grid.Columns = n == 0 ? 1 : (int)Math.Ceiling(Math.Sqrt(n));
        _emptyLabel.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;

        ApplyActiveHighlight();

        // レイアウト確定後に矩形を更新
        Dispatcher.BeginInvoke(new Action(UpdateThumbnailRects), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>各サムネイルの表示先を、対応タイルの表示領域（メインウィンドウ物理座標）に合わせる。</summary>
    private void UpdateThumbnailRects()
    {
        if (_selfHandle == IntPtr.Zero || _thumbs.Count == 0)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);

        foreach (var (handle, thumbId) in _thumbs)
        {
            if (!_tiles.TryGetValue(handle, out var tile))
                continue;

            var area = tile.ThumbnailArea;
            if (area.RenderSize.Width <= 0 || area.RenderSize.Height <= 0)
            {
                DwmThumbnail.SetVisible(thumbId, false);
                continue;
            }

            // タイル表示領域の矩形を、ウィンドウ本体クライアント座標（DIP）→ 物理pxへ
            var t = area.TransformToVisual(_root);
            var r = t.TransformBounds(new Rect(area.RenderSize));

            int x = (int)Math.Round(r.X * dpi.DpiScaleX);
            int y = (int)Math.Round(r.Y * dpi.DpiScaleY);
            int w = (int)Math.Round(r.Width * dpi.DpiScaleX);
            int h = (int)Math.Round(r.Height * dpi.DpiScaleY);

            // 内側に少し余白を取って枠と重ならないように
            const int pad = 3;
            DwmThumbnail.UpdateDestination(thumbId, x + pad, y + pad,
                Math.Max(1, w - pad * 2), Math.Max(1, h - pad * 2), visible: true);
        }
    }

    private void UnregisterThumb(IntPtr handle)
    {
        if (_thumbs.TryGetValue(handle, out var id))
        {
            DwmThumbnail.Unregister(id);
            _thumbs.Remove(handle);
        }
    }

    private void OnTileClicked(IntPtr handle)
    {
        var t = _monitors.GetTargetMonitor(_config.TargetMonitorIndex);
        NativeWindows.MoveMaximizeAndFocus(handle, t.WorkLeft, t.WorkTop, t.WorkWidth, t.WorkHeight);
    }

    protected override void OnClosed(EventArgs e)
    {
        _tracker?.Dispose();
        foreach (var id in _thumbs.Values)
            DwmThumbnail.Unregister(id);
        _thumbs.Clear();
        _tiles.Clear();
        _order.Clear();
        base.OnClosed(e);
    }
}
