using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using VSCodeLiveTiles.Controls;
using VSCodeLiveTiles.Interop;
using VSCodeLiveTiles.Services;

namespace VSCodeLiveTiles;

/// <summary>
/// 対象ウィンドウをライブサムネイルのタイルで並べるホストウィンドウ。
/// 表示は 2 モード（appsettings.json の displayMode）:
/// - window（既定）: 枠なし・最前面・自由リサイズの小型ウィンドウ。タイルはタスクバー風の
///   ストリップ（縦長→1列 / 横長→1行）。配置は window.json に記憶
/// - fullscreen: サブモニターに全画面ボーダーレス常駐（従来動作）。グリッドは ceil(√N) 列
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
    private readonly bool _windowMode;
    private System.Windows.Threading.DispatcherTimer? _placementSaveTimer;
    private MenuItem? _topmostMenuItem;

    // 右クリックメニュー（window モードのみ）: 擬似最大化とストリップの向き固定
    private MenuItem? _maximizeMenuItem;
    private readonly Dictionary<StripLayout, MenuItem> _layoutMenuItems = new();
    private StripLayout _layout = StripLayout.Auto;
    private bool _maximized;
    private (int X, int Y, int W, int H)? _restoreBounds;

    /// <summary>ストリップの向き。Auto はウィンドウのアスペクト比で決める（従来動作）。</summary>
    private enum StripLayout
    {
        Auto,
        Vertical,   // 1 列
        Horizontal, // 1 行
    }

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

    /// <summary>
    /// 手動並び順（メモリ上・HWND キー / SPEC §タイル並べ替え）。閉じたウィンドウの記憶も残すので、
    /// 実在するタイルより多い。表示順はこのリストから実在ハンドルだけを拾ったもの。
    /// </summary>
    private readonly List<IntPtr> _order = new();

    /// <summary>並びリストに残す最大件数（死んだ HWND を無限に溜めない）。</summary>
    private const int MaxRememberedOrder = 64;

    // タイルのドラッグ並べ替え（SPEC §タイル並べ替え）
    private ThumbnailTile? _dragTile;
    private Point _dragStart;
    private int _dragOriginalIndex = -1;
    private bool _dragging;
    // 一度ドラッグに入った押下は、離した先のタイルをクリック扱いにしない
    // （Esc で畳んだ後のボタン離しも含む）
    private bool _draggedSincePress;

    // thumbId → 最後に DWM へ適用した内容。同じなら RPC を投げない（UpdateThumbnailRects）
    private readonly Dictionary<IntPtr, ThumbApplied> _appliedThumbs = new();

    /// <summary>DWM へ適用済みの表示先矩形・ソースサイズ・不透明度（変わったときだけ更新する）。</summary>
    private readonly record struct ThumbApplied(
        int X, int Y, int W, int H, int SrcW, int SrcH, bool Visible, byte Opacity);

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

        _windowMode = !config.IsFullscreen;
        if (_windowMode)
        {
            // 枠なしのまま端ドラッグでリサイズできるようにする。
            // CaptionHeight=0 なのでクライアント領域のクリックは奪わない（タイルはそのまま押せる）
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 200;
            MinHeight = 150;
            Topmost = true; // 実際の値は RestoreWindowPlacement で window.json から復元
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
            });

            var outer = new Grid();
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var grip = BuildGripBar();
            Grid.SetRow(grip, 0);
            Grid.SetRow(_root, 1);
            outer.Children.Add(grip);
            outer.Children.Add(_root);
            Content = outer;

            SizeChanged += (_, _) => ApplyStripColumns();
            SizeChanged += (_, _) => SyncMaximizedState();
            SizeChanged += (_, _) => SchedulePlacementSave();
            LocationChanged += (_, _) => SchedulePlacementSave();
        }
        else
        {
            Content = _root;
        }

        ContextMenu = BuildContextMenu();

        // 並べ替えドラッグはタイルではなくグリッドで拾う。Preview（トンネル）なので
        // タイルのクリック処理より先に走り、ドラッグ確定時はここで Handled にして
        // クリック＝ウィンドウ切り替えの誤爆を止められる
        _grid.PreviewMouseLeftButtonDown += OnGridPreviewMouseLeftButtonDown;
        _grid.PreviewMouseMove += OnGridPreviewMouseMove;
        _grid.PreviewMouseLeftButtonUp += OnGridPreviewMouseLeftButtonUp;
        // 予期しないキャプチャ喪失（右クリックメニュー・他アプリの奪取など）は中断＝キャンセル扱い。
        // 正常なドロップは EndDrag が先に _dragging を降ろしてから解放するので、ここは no-op になる
        _grid.LostMouseCapture += (_, _) => CancelDrag();
        PreviewKeyDown += (_, e) =>
        {
            if (_dragging && e.Key == Key.Escape)
            {
                CancelDrag();
                e.Handled = true;
            }
        };

        // レイアウトが変わるたびにサムネイル矩形を追従させる
        _root.LayoutUpdated += (_, _) => UpdateThumbnailRects();
        SizeChanged += (_, _) => UpdateThumbnailRects();
        DpiChanged += (_, _) => UpdateThumbnailRects();
    }

    /// <summary>上端のドラッグ用グリップ帯。掴める場所が見えることを優先する（SPEC §3）。</summary>
    private Border BuildGripBar()
    {
        var normal = new SolidColorBrush(Color.FromRgb(0x1E, 0x1A, 0x18));
        var hover = new SolidColorBrush(Color.FromRgb(0x33, 0x2D, 0x2A));
        var bar = new Border
        {
            Height = 16,
            Background = normal,
            Cursor = Cursors.SizeAll,
            Child = new TextBlock
            {
                Text = "• • •",
                Foreground = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x78)),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        bar.MouseEnter += (_, _) => bar.Background = hover;
        bar.MouseLeave += (_, _) => bar.Background = normal;
        bar.MouseLeftButtonDown += (_, _) =>
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // ボタンが既に離れている（クリック連打時など）。移動しないだけでよい
            }
        };
        return bar;
    }

    /// <summary>
    /// 右クリックメニュー。「最前面に固定 / 終了」は両モード共通、
    /// 「最大化」「レイアウト」は window モードだけに出す（SPEC §右クリックメニューの拡張 §2）。
    /// </summary>
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        if (_windowMode)
        {
            _maximizeMenuItem = new MenuItem { Header = "最大化", IsCheckable = true };
            _maximizeMenuItem.Click += (_, _) => SetMaximized(_maximizeMenuItem.IsChecked);
            menu.Items.Add(_maximizeMenuItem);
            menu.Items.Add(new Separator());

            foreach (var (mode, header) in new[]
            {
                (StripLayout.Auto, "レイアウト: 自動"),
                (StripLayout.Vertical, "レイアウト: 縦1列"),
                (StripLayout.Horizontal, "レイアウト: 横1行"),
            })
            {
                var item = new MenuItem { Header = header, IsCheckable = true };
                item.Click += (_, _) => SetLayout(mode);
                _layoutMenuItems[mode] = item;
                menu.Items.Add(item);
            }
            UpdateLayoutMenuChecks();
            menu.Items.Add(new Separator());
        }

        _topmostMenuItem = new MenuItem { Header = "最前面に固定", IsCheckable = true, IsChecked = Topmost };
        _topmostMenuItem.Click += (_, _) =>
        {
            Topmost = _topmostMenuItem.IsChecked;
            SchedulePlacementSave();
        };
        var exit = new MenuItem { Header = "終了" };
        exit.Click += (_, _) => Close();

        menu.Items.Add(_topmostMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    /// <summary>ラジオ的に 1 つだけチェックを付ける（IsCheckable なのでクリックで反転した分を戻す）。</summary>
    private void UpdateLayoutMenuChecks()
    {
        foreach (var (mode, item) in _layoutMenuItems)
            item.IsChecked = mode == _layout;
    }

    private void SetLayout(StripLayout layout)
    {
        _layout = layout;
        UpdateLayoutMenuChecks();
        ApplyStripColumns(); // リサイズを待たずに即反映
        SchedulePlacementSave();
    }

    /// <summary>
    /// 擬似最大化。WindowStyle.None のまま WindowState.Maximized にするとタスクバーを覆うため、
    /// 作業領域へ SetWindowPos で広げる（SPEC §3）。解除は直前の位置・サイズへ。
    /// </summary>
    private void SetMaximized(bool maximize)
    {
        if (!_windowMode || _selfHandle == IntPtr.Zero)
            return;

        if (maximize)
        {
            if (!NativeWindows.TryGetWindowBounds(_selfHandle, out int x, out int y, out int w, out int h)
                || MonitorFor(x, y, w, h) is not { } m)
                return;
            _restoreBounds = (x, y, w, h);
            _maximized = true;
            SetBounds(m.WorkLeft, m.WorkTop, m.WorkWidth, m.WorkHeight);
        }
        else
        {
            _maximized = false;
            if (_restoreBounds is { } r)
                SetBounds(r.X, r.Y, r.W, r.H);
        }

        if (_maximizeMenuItem is not null)
            _maximizeMenuItem.IsChecked = _maximized;
        SchedulePlacementSave();
    }

    /// <summary>最大化中に端ドラッグでリサイズされたらチェックを外す（もう作業領域と一致しない）。</summary>
    private void SyncMaximizedState()
    {
        if (!_windowMode || !_maximized || _selfHandle == IntPtr.Zero)
            return;
        if (!NativeWindows.TryGetWindowBounds(_selfHandle, out int x, out int y, out int w, out int h))
            return;
        if (MonitorFor(x, y, w, h) is { } m
            && x == m.WorkLeft && y == m.WorkTop && w == m.WorkWidth && h == m.WorkHeight)
            return;

        _maximized = false;
        if (_maximizeMenuItem is not null)
            _maximizeMenuItem.IsChecked = false;
    }

    /// <summary>矩形が最も多く重なっているモニター（どれとも重ならなければ最初の 1 枚）。</summary>
    private MonitorService.MonitorInfoEx? MonitorFor(int x, int y, int w, int h)
    {
        var mons = _monitors.GetMonitors();
        if (mons.Count == 0)
            return null;
        return mons
            .OrderByDescending(m =>
            {
                long ow = Math.Max(0, Math.Min(x + w, m.WorkLeft + m.WorkWidth) - Math.Max(x, m.WorkLeft));
                long oh = Math.Max(0, Math.Min(y + h, m.WorkTop + m.WorkHeight) - Math.Max(y, m.WorkTop));
                return ow * oh;
            })
            .First();
    }

    private void SetBounds(int x, int y, int w, int h)
        => NativeWindows.SetWindowPos(_selfHandle, NativeWindows.HWND_TOP, x, y, w, h,
            NativeWindows.SWP_NOZORDER | NativeWindows.SWP_NOACTIVATE | NativeWindows.SWP_SHOWWINDOW);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _selfHandle = new WindowInteropHelper(this).Handle;

        if (_windowMode)
            RestoreWindowPlacement();
        else
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
            if (_windowMode)
                EnsureVisiblePlacement();
            else
                PositionOnWidgetMonitor();
        }));

    /// <summary>
    /// window.json の保存値でウィンドウを配置する（物理px）。初回・値なしはプライマリ作業領域の
    /// 右端に縦長で置く。保存値がどのモニターにも掛かっていなければ最寄り作業領域へクランプ。
    /// モニター 0 枚のときは配置を保留し、DisplaySettingsChanged を待つ（fullscreen と同じ方針）。
    /// </summary>
    private void RestoreWindowPlacement()
    {
        var saved = WindowPlacementStore.Load();
        Topmost = saved?.Topmost ?? true;
        if (_topmostMenuItem is not null)
            _topmostMenuItem.IsChecked = Topmost;

        _layout = saved?.LayoutMode switch
        {
            "vertical" => StripLayout.Vertical,
            "horizontal" => StripLayout.Horizontal,
            _ => StripLayout.Auto,
        };
        UpdateLayoutMenuChecks();
        ApplyStripColumns();

        _maximized = saved?.Maximized ?? false;
        if (saved is { RestoreWidth: > 0, RestoreHeight: > 0 })
            _restoreBounds = (saved.RestoreX, saved.RestoreY, saved.RestoreWidth, saved.RestoreHeight);
        if (_maximizeMenuItem is not null)
            _maximizeMenuItem.IsChecked = _maximized;

        var mons = _monitors.GetMonitors();
        if (mons.Count == 0)
        {
            Log.Info("モニターが検出できないため配置を保留します（構成が戻り次第配置し直します）");
            return;
        }

        int x, y, w, h;
        if (saved is { Width: > 0, Height: > 0 })
        {
            (x, y, w, h) = ClampToWorkArea(saved.X, saved.Y, saved.Width, saved.Height, mons);
        }
        else
        {
            var pri = mons.FirstOrDefault(m => m.IsPrimary) ?? mons[0];
            w = 420;
            h = pri.WorkHeight * 6 / 10;
            x = pri.WorkLeft + pri.WorkWidth - w - 16;
            y = pri.WorkTop + (pri.WorkHeight - h) / 2;
        }
        NativeWindows.SetWindowPos(_selfHandle, NativeWindows.HWND_TOP, x, y, w, h,
            NativeWindows.SWP_NOZORDER | NativeWindows.SWP_NOACTIVATE | NativeWindows.SWP_SHOWWINDOW);
    }

    /// <summary>モニター構成変化後、ウィンドウが画面外に取り残されていたら作業領域内へ戻す。</summary>
    private void EnsureVisiblePlacement()
    {
        if (_selfHandle == IntPtr.Zero)
            return;
        var mons = _monitors.GetMonitors();
        if (mons.Count == 0
            || !NativeWindows.TryGetWindowBounds(_selfHandle, out int x, out int y, out int w, out int h))
            return;

        var c = ClampToWorkArea(x, y, w, h, mons);
        if (c != (x, y, w, h))
            NativeWindows.SetWindowPos(_selfHandle, NativeWindows.HWND_TOP, c.X, c.Y, c.W, c.H,
                NativeWindows.SWP_NOZORDER | NativeWindows.SWP_NOACTIVATE | NativeWindows.SWP_SHOWWINDOW);
    }

    /// <summary>矩形がどの作業領域にも掛かっていなければ、最寄りモニターの作業領域内へ収める。</summary>
    private static (int X, int Y, int W, int H) ClampToWorkArea(
        int x, int y, int w, int h, IReadOnlyList<MonitorService.MonitorInfoEx> mons)
    {
        bool visible = mons.Any(m =>
            x < m.WorkLeft + m.WorkWidth && x + w > m.WorkLeft &&
            y < m.WorkTop + m.WorkHeight && y + h > m.WorkTop);
        if (visible)
            return (x, y, w, h);

        var near = mons.OrderBy(m =>
        {
            long dx = x + w / 2 - (m.WorkLeft + m.WorkWidth / 2);
            long dy = y + h / 2 - (m.WorkTop + m.WorkHeight / 2);
            return dx * dx + dy * dy;
        }).First();

        w = Math.Min(w, near.WorkWidth);
        h = Math.Min(h, near.WorkHeight);
        x = Math.Clamp(x, near.WorkLeft, near.WorkLeft + near.WorkWidth - w);
        y = Math.Clamp(y, near.WorkTop, near.WorkTop + near.WorkHeight - h);
        return (x, y, w, h);
    }

    /// <summary>移動・リサイズ確定から 1 秒後に保存（連続イベントの書き込みを 1 回にまとめる）。</summary>
    private void SchedulePlacementSave()
    {
        if (!_windowMode || _selfHandle == IntPtr.Zero)
            return;
        if (_placementSaveTimer is null)
        {
            _placementSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _placementSaveTimer.Tick += (_, _) =>
            {
                _placementSaveTimer!.Stop();
                SavePlacementNow();
            };
        }
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private void SavePlacementNow()
    {
        if (!_windowMode || _selfHandle == IntPtr.Zero)
            return;
        if (!NativeWindows.TryGetWindowBounds(_selfHandle, out int x, out int y, out int w, out int h))
            return;

        var r = _restoreBounds ?? (x, y, w, h);
        var layout = _layout switch
        {
            StripLayout.Vertical => "vertical",
            StripLayout.Horizontal => "horizontal",
            _ => "auto",
        };
        WindowPlacementStore.Save(new WindowPlacement(x, y, w, h, Topmost,
            _maximized, r.X, r.Y, r.W, r.H, layout));
    }

    /// <summary>window モードのストリップの向きを現在の設定・サイズに合わせる。</summary>
    private void ApplyStripColumns()
    {
        if (!_windowMode)
            return;
        int cols = GridColumnsFor(_tiles.Count);
        if (_grid.Columns != cols)
            _grid.Columns = cols;
    }

    /// <summary>
    /// タイル数 n に対する列数。window モードはタスクバー風ストリップで、
    /// メニューで固定されていればその向き、自動ならアスペクト比（縦長→1列 / 横長→1行）。
    /// fullscreen は従来の ceil(√n) グリッド。
    /// </summary>
    private int GridColumnsFor(int n)
    {
        if (n <= 1)
            return 1;
        if (!_windowMode)
            return (int)Math.Ceiling(Math.Sqrt(n));
        return _layout switch
        {
            StripLayout.Vertical => 1,
            StripLayout.Horizontal => n,
            _ => ActualHeight > ActualWidth ? 1 : n,
        };
    }

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
        var byHandle = windows.ToDictionary(w => w.Handle);

        // 消えたウィンドウ：サムネイル解放 + タイル除去。
        // 並びリストからは消さない（開き直しても位置を保つ / SPEC §タイル並べ替え §3）
        foreach (var handle in _tiles.Keys.Where(h => !byHandle.ContainsKey(h)).ToList())
        {
            UnregisterThumb(handle);
            if (_tiles.TryGetValue(handle, out var gone))
            {
                gone.SetCcState(CcState.None, default, null); // 明滅クロックを残さない
                _grid.Children.Remove(gone);
                _tiles.Remove(handle);
            }
        }

        // 掴んでいたタイルのウィンドウが消えたらドラッグを畳む（並びは現状のまま確定）
        if (_dragTile is not null && !byHandle.ContainsKey(_dragTile.Handle))
            EndDrag();

        // 新規ウィンドウは並びリストの末尾へ。同時に複数現れたときは HWND 昇順
        // （＝手を付けていない状態では従来と同じ並びになる）
        foreach (var info in windows.OrderBy(w => (long)w.Handle))
        {
            if (!_order.Contains(info.Handle))
                _order.Add(info.Handle);
        }
        PruneOrder(byHandle.Keys);

        var desired = _order.Where(byHandle.ContainsKey).Select(h => byHandle[h]).ToList();

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
                _grid.Children.Add(tile); // 並び順は末尾の SyncTileOrder で揃える
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

        SyncTileOrder();

        int n = desired.Count;
        _grid.Columns = GridColumnsFor(n);
        _emptyLabel.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;

        ApplyActiveHighlight();
        ApplyCcStates(); // 新規タイル・タイトル変化（照合キー変化）に追従

        Log.SlowIf("タイル再構築（OnWindowsUpdated）", started, Log.SlowMs);

        // レイアウト確定後に矩形を更新
        Dispatcher.BeginInvoke(new Action(UpdateThumbnailRects), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 並びリストが増え続けないよう、上限を超えた分だけ死んだ HWND を並びの先頭側から捨てる。
    /// 実在するウィンドウの記憶は絶対に落とさない。
    /// ドラッグ中は動かさない（_dragOriginalIndex が指す位置がずれてキャンセル復元が狂う）。
    /// </summary>
    private void PruneOrder(IEnumerable<IntPtr> alive)
    {
        if (_dragging || _order.Count <= MaxRememberedOrder)
            return;
        var live = new HashSet<IntPtr>(alive);
        for (int i = 0; i < _order.Count && _order.Count > MaxRememberedOrder;)
        {
            if (live.Contains(_order[i]))
                i++;
            else
                _order.RemoveAt(i);
        }
    }

    /// <summary>グリッドの子の並びを並びリスト順に合わせる（タイル数は高々数十なので素直に詰める）。</summary>
    private void SyncTileOrder()
    {
        int index = 0;
        foreach (var handle in _order)
        {
            if (!_tiles.TryGetValue(handle, out var tile))
                continue;
            int cur = _grid.Children.IndexOf(tile);
            if (cur != index)
            {
                if (cur >= 0)
                    _grid.Children.RemoveAt(cur);
                _grid.Children.Insert(Math.Min(index, _grid.Children.Count), tile);
            }
            index++;
        }
    }

    /// <summary>押下地点を覚えるだけ。ここではまだ掴まない（閾値未満はクリック＝切り替え）。</summary>
    private void OnGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // フラグの寿命は「1 回の押下」。キャプチャを奪われて Up が届かなかった前回の残骸で
        // 次のクリックを飲み込まないよう、押下のたびに必ず落とす
        _draggedSincePress = false;
        if (_dragging)
            return;
        _dragTile = FindTile(e.OriginalSource);
        _dragStart = e.GetPosition(this);
    }

    /// <summary>閾値超えでドラッグ開始、以降はカーソル下のタイル位置へ挿入していく。</summary>
    private void OnGridPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTile is null)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        var pos = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            BeginDrag();
        }

        // PreviewKeyDown を取りこぼしたとき（メニューなど別要素がフォーカスを持っている場合）の保険。
        // WPF の KeyboardDevice 経由なので、アプリ自体が非アクティブなら効かない
        if (Keyboard.IsKeyDown(Key.Escape))
        {
            CancelDrag();
            return;
        }
        MoveDragTileTo(pos);
    }

    /// <summary>ドロップ確定。ウィジェット外で離した場合は元の並びに戻す（SPEC §2）。</summary>
    private void OnGridPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool dragged = _draggedSincePress;
        _draggedSincePress = false;

        if (_dragging)
        {
            if (new Rect(RenderSize).Contains(e.GetPosition(this)))
                EndDrag();
            else
                CancelDrag();
        }
        _dragTile = null; // 閾値未満＝クリック。タイル側の切り替え処理へ通す

        // ドラッグした押下はトンネル段階で止めて、タイルの PreviewMouseLeftButtonUp
        // （＝ウィンドウ切り替え）を発火させない。Esc で畳んだ後の離しもここに来る
        if (dragged)
            e.Handled = true;
    }

    private void BeginDrag()
    {
        if (_dragTile is null)
            return;
        _dragging = true;
        _draggedSincePress = true;
        _dragOriginalIndex = _order.IndexOf(_dragTile.Handle);
        _dragTile.SetDragging(true);
        _grid.CaptureMouse(); // ウィジェット外へ出ても移動・ドロップを追える
        UpdateThumbnailRects(); // サムネイル本体も半透明に
    }

    /// <summary>カーソル下のタイルの位置へ、掴んだタイルを割り込ませる（挿入方式・スワップではない）。</summary>
    private void MoveDragTileTo(Point pos)
    {
        if (_dragTile is null)
            return;
        var target = TileAt(pos);
        if (target is null || ReferenceEquals(target, _dragTile))
            return;

        int from = _order.IndexOf(_dragTile.Handle);
        int to = _order.IndexOf(target.Handle);
        if (from < 0 || to < 0 || from == to)
            return;

        _order.RemoveAt(from);
        int t = _order.IndexOf(target.Handle);
        _order.Insert(from < to ? t + 1 : t, _dragTile.Handle);
        SyncTileOrder();
    }

    /// <summary>現在の並びのままドラッグを終える（正常なドロップ・対象ウィンドウ消失）。</summary>
    private void EndDrag()
    {
        _dragOriginalIndex = -1;
        var tile = _dragTile;
        _dragTile = null;
        if (!_dragging)
            return;

        _dragging = false; // ReleaseMouseCapture が LostMouseCapture を呼ぶので先に降ろす
        tile?.SetDragging(false);
        if (_grid.IsMouseCaptured)
            _grid.ReleaseMouseCapture();
        UpdateThumbnailRects();
    }

    /// <summary>掴んだタイルを掴む前の位置へ戻してから終える（Esc / ウィジェット外ドロップ）。</summary>
    private void CancelDrag()
    {
        if (!_dragging || _dragTile is null)
        {
            EndDrag();
            return;
        }

        var handle = _dragTile.Handle;
        int origin = _dragOriginalIndex;
        int cur = _order.IndexOf(handle);
        if (cur >= 0 && origin >= 0)
        {
            _order.RemoveAt(cur);
            _order.Insert(Math.Clamp(origin, 0, _order.Count), handle);
        }
        SyncTileOrder(); // 並びを戻してから畳む（先に畳むと復元前のレイアウトで DWM を 1 往復叩く）
        EndDrag();
    }

    /// <summary>指定座標（ウィンドウ座標）に重なっているタイル。</summary>
    private ThumbnailTile? TileAt(Point pos)
    {
        foreach (var tile in _tiles.Values)
        {
            if (!tile.IsVisible)
                continue;
            var r = tile.TransformToVisual(this).TransformBounds(new Rect(tile.RenderSize));
            if (r.Contains(pos))
                return tile;
        }
        return null;
    }

    /// <summary>イベント発生元（キャプションの TextBlock 等）から親方向にタイルを探す。</summary>
    private static ThumbnailTile? FindTile(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (node is ThumbnailTile tile)
                return tile;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
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

            // タイル表示領域の矩形を、ウィンドウ本体クライアント座標（DIP）→ 物理pxへ。
            // DWM の表示先はクライアント座標基準。_root 基準で計算すると window モードの
            // グリップ帯（上端 16px）の分だけずれてタイルからはみ出す
            var t = area.TransformToVisual(this);
            var r = t.TransformBounds(new Rect(area.RenderSize));

            int x = (int)Math.Round(r.X * dpi.DpiScaleX);
            int y = (int)Math.Round(r.Y * dpi.DpiScaleY);
            int w = (int)Math.Round(r.Width * dpi.DpiScaleX);
            int h = (int)Math.Round(r.Height * dpi.DpiScaleY);

            NativeWindows.TryGetWindowSize(handle, out int srcW, out int srcH);
            // 掴んでいるタイルはサムネイルも半透明にする（WPF の Opacity は DWM 合成に効かない）
            byte opacity = tile.IsDragging ? (byte)(255 * ThumbnailTile.DragOpacity) : (byte)255;
            var current = new ThumbApplied(x, y, w, h, srcW, srcH, Visible: true, opacity);
            if (_appliedThumbs.TryGetValue(thumbId, out var applied) && applied == current)
                continue;

            // 内側に少し余白を取って枠と重ならないように
            const int pad = 3;
            DwmThumbnail.UpdateDestination(thumbId, x + pad, y + pad,
                Math.Max(1, w - pad * 2), Math.Max(1, h - pad * 2), visible: true, opacity: opacity);
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
        _placementSaveTimer?.Stop();
        SavePlacementNow();
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
