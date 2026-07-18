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
    private UiThreadWatchdog? _watchdog;
    private IntPtr _selfHandle;
    private IntPtr _activeHandle;

    // CC 状態バッジ（events.jsonl が読めない環境では null のまま＝機能ごと無効）
    private CcEventsReader? _ccReader;
    private CcStateTracker? _ccTracker;
    private System.Windows.Threading.DispatcherTimer? _badgeTimer;
    private System.Windows.Threading.DispatcherTimer? _ccSweepTimer;

    // handle → タイル。thumbs: source handle → DWM サムネイル ID（最小化中は登録しない）
    private readonly Dictionary<IntPtr, ThumbnailTile> _tiles = new();
    private readonly Dictionary<IntPtr, IntPtr> _thumbs = new();
    private readonly List<IntPtr> _order = new();

    // thumbId → 最後に DWM へ適用した内容。同じなら RPC を投げない（UpdateThumbnailRects）
    private readonly Dictionary<IntPtr, ThumbApplied> _appliedThumbs = new();

    /// <summary>DWM へ適用済みの表示先矩形とソースサイズ（どちらかが変わったときだけ更新する）。</summary>
    private readonly record struct ThumbApplied(int X, int Y, int W, int H, int SrcW, int SrcH, bool Visible);

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

        StartCcBadges();

        // モニターの抜き差し・RDP 切断/再接続・解像度変更で構成が変わったら配置し直す。
        // イベントは SystemEvents の専用スレッドから来るので UI スレッドへ渡す
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _watchdog = new UiThreadWatchdog(Dispatcher);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(() =>
        {
            Log.Info("モニター構成が変わったため配置し直します");
            PositionOnWidgetMonitor();
        }));

    /// <summary>
    /// CC 状態バッジの監視を開始する。フック未導入・読み取り失敗時は静かに無効化し、
    /// タイル・クリックの既存機能には影響させない（SPEC §6）。
    /// </summary>
    private void StartCcBadges()
    {
        CcEventsReader? reader = null;
        try
        {
            var tracker = new CcStateTracker();
            reader = new CcEventsReader();
            reader.Received += tracker.Apply;
            tracker.Changed += ApplyCcStates;
            if (!reader.Start())
            {
                reader.Dispose();
                return;
            }
            _ccTracker = tracker;
            _ccReader = reader;

            // 死んだセッションの掃除と鮮度切れの反映は時間経過で起きる（イベントが来ない）ため、
            // イベント駆動の Apply とは別に定期的に回す
            _ccSweepTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(1),
            };
            _ccSweepTimer.Tick += (_, _) =>
            {
                _ccTracker?.Sweep();
                ApplyCcStates();
            };
            _ccSweepTimer.Start();
        }
        catch
        {
            reader?.Dispose();
            _ccSweepTimer?.Stop();
            _ccSweepTimer = null;
            _ccReader = null;
            _ccTracker = null;
        }
    }

    /// <summary>
    /// 各タイルへ代表状態を配布し、経過表示（待ちバッジ・セッション時計）を持つタイルの
    /// 有無で 1 秒タイマーを回す/止める。
    /// </summary>
    private void ApplyCcStates()
    {
        if (_ccTracker is null)
            return;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool anyElapsed = false;
        foreach (var tile in _tiles.Values)
        {
            var resolved = _ccTracker.Resolve(tile.CaptionText);
            tile.SetCcState(resolved?.State ?? CcState.None, resolved?.Since ?? default, resolved?.Started);
            anyElapsed |= tile.IsCcWaiting || tile.HasSessionClock;
        }
        Log.SlowIf($"CC 状態の配布（{_tiles.Count} タイル）", started, Log.SlowMs);

        if (anyElapsed)
        {
            if (_badgeTimer is null)
            {
                _badgeTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1),
                };
                _badgeTimer.Tick += (_, _) =>
                {
                    foreach (var tile in _tiles.Values)
                        tile.RefreshElapsed();
                };
            }
            _badgeTimer.Start();
        }
        else
        {
            _badgeTimer?.Stop();
        }
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

    /// <summary>
    /// ウィジェット用モニターの作業領域へ物理ピクセルで配置する（DPI 変換を避ける）。
    /// モニターが 1 枚もない（RDP 切断中など）ときは何もせず、構成が戻ったときの
    /// DisplaySettingsChanged で配置し直されるのを待つ。
    /// </summary>
    private void PositionOnWidgetMonitor()
    {
        if (_monitors.GetWidgetMonitor(_config.WidgetMonitorIndex) is not { } m)
        {
            Log.Info("モニターが検出できないため配置を保留します（構成が戻り次第配置し直します）");
            return;
        }
        NativeWindows.SetWindowPos(_selfHandle, NativeWindows.HWND_TOP,
            m.WorkLeft, m.WorkTop, m.WorkWidth, m.WorkHeight,
            NativeWindows.SWP_NOZORDER | NativeWindows.SWP_NOACTIVATE | NativeWindows.SWP_SHOWWINDOW);
    }

    private void OnWindowsUpdated(IReadOnlyList<NativeWindows.WindowInfo> windows)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
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
                // DWM への RPC。dwm.exe が詰まっていれば UI スレッドごと待たされる
                long tReg = System.Diagnostics.Stopwatch.GetTimestamp();
                var id = DwmThumbnail.Register(_selfHandle, info.Handle);
                Log.SlowIf($"DwmRegisterThumbnail(hwnd=0x{info.Handle:X})", tReg, Log.SlowMs);
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
        ApplyCcStates(); // 新規タイル・タイトル変化（照合キー変化）に追従

        Log.SlowIf("タイル再構築（OnWindowsUpdated）", started, Log.SlowMs);

        // レイアウト確定後に矩形を更新
        Dispatcher.BeginInvoke(new Action(UpdateThumbnailRects), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 各サムネイルの表示先を、対応タイルの表示領域（メインウィンドウ物理座標）に合わせる。
    ///
    /// LayoutUpdated ごとに走る（バッジの毎秒更新で最低 1 回/秒）が、DWM への RPC は
    /// 表示先矩形かソースサイズが前回適用時から変わったときだけ投げる。
    /// dwm.exe を UI スレッドで待つ経路なので、定常状態で毎秒叩き続けない。
    /// ソースサイズ（レターボックス比率に影響）の変化検知は GetWindowRect —
    /// 相手プロセスを待たない読み取りだけで済ませる。
    /// </summary>
    private void UpdateThumbnailRects()
    {
        if (_selfHandle == IntPtr.Zero || _thumbs.Count == 0)
            return;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var dpi = VisualTreeHelper.GetDpi(this);

        foreach (var (handle, thumbId) in _thumbs)
        {
            if (!_tiles.TryGetValue(handle, out var tile))
                continue;

            var area = tile.ThumbnailArea;
            if (area.RenderSize.Width <= 0 || area.RenderSize.Height <= 0)
            {
                if (!_appliedThumbs.TryGetValue(thumbId, out var hidden) || hidden.Visible)
                {
                    DwmThumbnail.SetVisible(thumbId, false);
                    _appliedThumbs[thumbId] = default; // Visible=false
                }
                continue;
            }

            // タイル表示領域の矩形を、ウィンドウ本体クライアント座標（DIP）→ 物理pxへ
            var t = area.TransformToVisual(_root);
            var r = t.TransformBounds(new Rect(area.RenderSize));

            int x = (int)Math.Round(r.X * dpi.DpiScaleX);
            int y = (int)Math.Round(r.Y * dpi.DpiScaleY);
            int w = (int)Math.Round(r.Width * dpi.DpiScaleX);
            int h = (int)Math.Round(r.Height * dpi.DpiScaleY);

            NativeWindows.TryGetWindowSize(handle, out int srcW, out int srcH);
            var current = new ThumbApplied(x, y, w, h, srcW, srcH, Visible: true);
            if (_appliedThumbs.TryGetValue(thumbId, out var applied) && applied == current)
                continue;

            // 内側に少し余白を取って枠と重ならないように
            const int pad = 3;
            DwmThumbnail.UpdateDestination(thumbId, x + pad, y + pad,
                Math.Max(1, w - pad * 2), Math.Max(1, h - pad * 2), visible: true);
            _appliedThumbs[thumbId] = current;
        }

        Log.SlowIf($"サムネイル矩形の追従（DWM 更新 {_thumbs.Count} 枚）", started, Log.SlowMs);
    }

    private void UnregisterThumb(IntPtr handle)
    {
        if (_thumbs.TryGetValue(handle, out var id))
        {
            DwmThumbnail.Unregister(id);
            _thumbs.Remove(handle);
            _appliedThumbs.Remove(id);
        }
    }

    /// <summary>
    /// タイルのクリック。切り替え自体は UI スレッドで待たない。
    /// 末尾の SetForegroundWindow は相手（VSCode）が固まっていると 5 秒戻ってこず、
    /// UI スレッドで待つとウィジェット自身がゴーストウィンドウに置き換わるため
    /// （v0.8.1。フォアグラウンド化の権利はプロセス単位なので別スレッドでも通る）。
    /// </summary>
    private void OnTileClicked(IntPtr handle)
    {
        if (_monitors.GetTargetMonitor(_config.TargetMonitorIndex) is not { } t)
            return; // モニターが 1 枚もなければ移動先がない
        Task.Run(() =>
        {
            try
            {
                NativeWindows.MoveMaximizeAndFocus(handle, t.WorkLeft, t.WorkTop, t.WorkWidth, t.WorkHeight);
            }
            catch (Exception ex)
            {
                Log.Error($"ウィンドウ切り替えに失敗（hwnd=0x{handle:X}）", ex);
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        // SystemEvents は static イベントなので、外さないとウィンドウが回収されない
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _watchdog?.Dispose();
        _badgeTimer?.Stop();
        _ccSweepTimer?.Stop();
        _ccReader?.Dispose();
        _tracker?.Dispose();
        foreach (var id in _thumbs.Values)
            DwmThumbnail.Unregister(id);
        _thumbs.Clear();
        _appliedThumbs.Clear();
        _tiles.Clear();
        _order.Clear();
        base.OnClosed(e);
    }
}
