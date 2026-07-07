using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VSCodeLiveTiles.Interop;
using VSCodeLiveTiles.Services;

namespace VSCodeLiveTiles.Controls;

/// <summary>
/// 1 ウィンドウ分のタイル（純 WPF）。上段はサムネイルの表示領域（実際の描画は
/// MainWindow が DWM でメインウィンドウ本体に合成する＝この領域の上に重なる）、
/// 下段はキャプション帯。DWM サムネイルは入力を奪わないので、クリックは WPF 側で取れる。
/// </summary>
public sealed class ThumbnailTile : Border
{
    private static readonly Brush TileBg = new SolidColorBrush(Color.FromRgb(0x20, 0x1A, 0x18));
    private static readonly Brush CaptionBg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush CaptionFg = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
    private static readonly Brush TileBorder = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly Brush PlaceholderFg = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x88));
    // ホバー枠（控えめなグレー）／アクティブ強調（VSCode ブルー）
    private static readonly Brush HoverBorder = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x72));
    private static readonly Brush ActiveBorder = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
    private static readonly Brush ActiveCaptionBg = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
    private static readonly Brush ActiveCaptionFg = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));

    // CC 状態バッジの色（SPEC §5）。質問=黄 / 承認=橙 / 完了=緑 / 作業中=青
    private static readonly Color QuestionColor = Color.FromRgb(0xE5, 0xC0, 0x7B);
    private static readonly Color PermissionColor = Color.FromRgb(0xD1, 0x9A, 0x66);
    private static readonly Brush QuestionFg = new SolidColorBrush(QuestionColor);
    private static readonly Brush PermissionFg = new SolidColorBrush(PermissionColor);
    private static readonly Brush DoneFg = new SolidColorBrush(Color.FromRgb(0x98, 0xC3, 0x79));
    private static readonly Brush WorkingFg = new SolidColorBrush(Color.FromRgb(0x61, 0xAF, 0xEF));

    private readonly string[] _captionSuffixes;
    private readonly Border _contentArea;
    private readonly Border _captionBar;
    private readonly TextBlock _caption;
    private readonly TextBlock _badge;
    private readonly TextBlock _placeholder;

    private bool _isHover;
    private bool _isActive;
    private CcState _ccState = CcState.None;
    private DateTime _ccSince;
    private SolidColorBrush? _glowBrush; // 待ちグロー用（Opacity をアニメーションするため非 Freeze）

    public IntPtr Handle { get; private set; }
    public bool IsMinimizedState { get; private set; }

    /// <summary>サフィックス除去後のキャプション（CC 状態の照合キー）。</summary>
    public string CaptionText => _caption.Text;

    /// <summary>質問待ち・承認待ちか（経過時間タイマーの稼働判定に使う）。</summary>
    public bool IsCcWaiting => _ccState is CcState.WaitingQuestion or CcState.WaitingPermission;

    /// <summary>DWM サムネイルを合成する矩形の基準要素（メインウィンドウがここの位置を測る）。</summary>
    public UIElement ThumbnailArea => _contentArea;

    /// <summary>タイルがクリックされた（対象ウィンドウのハンドルを渡す）。</summary>
    public event Action<IntPtr>? Clicked;

    public ThumbnailTile(AppConfig config)
    {
        _captionSuffixes = config.CaptionSuffixesToStrip;

        Margin = new Thickness(6);
        Background = TileBg;
        BorderBrush = TileBorder;
        BorderThickness = new Thickness(2); // 太さは固定（色だけ変える）→ サムネイルがズレない
        CornerRadius = new CornerRadius(4);
        SnapsToDevicePixels = true;
        Cursor = Cursors.Hand;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _placeholder = new TextBlock
        {
            Text = "最小化中",
            Foreground = PlaceholderFg,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        var contentInner = new Grid();
        contentInner.Children.Add(_placeholder);

        _contentArea = new Border { Background = TileBg, ClipToBounds = true, Child = contentInner };
        Grid.SetRow(_contentArea, 0);
        root.Children.Add(_contentArea);

        _caption = new TextBlock
        {
            Foreground = CaptionFg,
            FontSize = 24,
            Margin = new Thickness(8, 3, 8, 3),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _badge = new TextBlock
        {
            FontSize = 20,
            Margin = new Thickness(0, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        var captionRow = new Grid();
        captionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        captionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_caption, 0);
        Grid.SetColumn(_badge, 1);
        captionRow.Children.Add(_caption);
        captionRow.Children.Add(_badge);
        _captionBar = new Border { Background = CaptionBg, Child = captionRow };
        Grid.SetRow(_captionBar, 1);
        root.Children.Add(_captionBar);

        Child = root;

        // タイル全体でクリックを拾う（サムネイルは入力を奪わないので content 上でも発火する）
        PreviewMouseLeftButtonUp += (_, _) => RaiseClicked();
        MouseEnter += (_, _) => { _isHover = true; ApplyHighlight(); };
        MouseLeave += (_, _) => { _isHover = false; ApplyHighlight(); };
    }

    /// <summary>アクティブ（前面）ウィンドウかどうかを設定して強調表示を更新する。</summary>
    public void SetActive(bool active)
    {
        if (_isActive == active)
            return;
        _isActive = active;
        ApplyHighlight();
    }

    // 枠の優先度：待ちグロー ＞ アクティブ ＞ ホバー ＞ 通常（SPEC §5）
    // キャプション帯の配色はアクティブ状態のまま（待ち中でも前面かどうかは見えるように）
    private void ApplyHighlight()
    {
        if (IsCcWaiting)
        {
            BorderBrush = GetOrStartGlow(_ccState == CcState.WaitingQuestion ? QuestionColor : PermissionColor);
        }
        else
        {
            StopGlow();
            BorderBrush = _isActive ? ActiveBorder : _isHover ? HoverBorder : TileBorder;
        }

        if (_isActive)
        {
            _captionBar.Background = ActiveCaptionBg;
            _caption.Foreground = ActiveCaptionFg;
        }
        else
        {
            _captionBar.Background = CaptionBg;
            _caption.Foreground = CaptionFg;
        }
    }

    /// <summary>
    /// 待ちグロー用の明滅ブラシを返す（約 1Hz で不透明度を往復）。
    /// BorderThickness は 2 固定のまま色だけ変える（太さを変えるとサムネイルがズレる）。
    /// </summary>
    private Brush GetOrStartGlow(Color color)
    {
        if (_glowBrush is not null && _glowBrush.Color == color)
            return _glowBrush;

        StopGlow();
        _glowBrush = new SolidColorBrush(color);
        var blink = new DoubleAnimation(1.0, 0.30, TimeSpan.FromMilliseconds(500))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        _glowBrush.BeginAnimation(Brush.OpacityProperty, blink);
        return _glowBrush;
    }

    private void StopGlow()
    {
        if (_glowBrush is null)
            return;
        _glowBrush.BeginAnimation(Brush.OpacityProperty, null);
        _glowBrush = null;
    }

    /// <summary>CC 状態を設定してバッジ・枠グローを更新する（MainWindow が配布）。</summary>
    public void SetCcState(CcState state, DateTime since)
    {
        if (_ccState == state && _ccSince == since)
            return;
        _ccState = state;
        _ccSince = since;
        UpdateBadge();
        ApplyHighlight();
    }

    /// <summary>待ちバッジの経過時間表示を更新する（MainWindow の 1 秒タイマーから呼ばれる）。</summary>
    public void RefreshBadgeClock()
    {
        if (IsCcWaiting)
            UpdateBadge();
    }

    private void UpdateBadge()
    {
        switch (_ccState)
        {
            case CcState.WaitingQuestion:
                _badge.Text = $"❓ 質問 {FormatElapsed()}";
                _badge.Foreground = QuestionFg;
                break;
            case CcState.WaitingPermission:
                _badge.Text = $"🔑 承認 {FormatElapsed()}";
                _badge.Foreground = PermissionFg;
                break;
            case CcState.Done:
                _badge.Text = "✔ 完了";
                _badge.Foreground = DoneFg;
                break;
            case CcState.Working:
                _badge.Text = "● 作業中";
                _badge.Foreground = WorkingFg;
                break;
            default:
                _badge.Visibility = Visibility.Collapsed;
                return;
        }
        _badge.Visibility = Visibility.Visible;
    }

    private string FormatElapsed()
    {
        var e = DateTime.Now - _ccSince;
        if (e < TimeSpan.Zero)
            e = TimeSpan.Zero;
        return $"{(int)e.TotalMinutes}:{e.Seconds:00}";
    }

    public void Bind(NativeWindows.WindowInfo info)
    {
        Handle = info.Handle;
        SetMinimized(info.IsMinimized);
        _caption.Text = StripSuffix(info.Title);
        ToolTip = info.Title;
    }

    /// <summary>タイトルだけ更新（タブ切替など高頻度。サムネイルには触れない）。</summary>
    public void UpdateTitle(NativeWindows.WindowInfo info)
    {
        _caption.Text = StripSuffix(info.Title);
        ToolTip = info.Title;
    }

    /// <summary>最小化状態を反映（プレースホルダの表示切替）。</summary>
    public void SetMinimized(bool minimized)
    {
        IsMinimizedState = minimized;
        _placeholder.Visibility = minimized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RaiseClicked()
    {
        if (Handle != IntPtr.Zero)
            Clicked?.Invoke(Handle);
    }

    private string StripSuffix(string title)
    {
        foreach (var s in _captionSuffixes)
        {
            if (title.EndsWith(s, StringComparison.Ordinal))
                return title[..^s.Length];
        }
        return title;
    }
}
