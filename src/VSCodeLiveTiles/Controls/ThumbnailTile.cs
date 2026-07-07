using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VSCodeLiveTiles.Interop;

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

    private readonly string[] _captionSuffixes;
    private readonly Border _contentArea;
    private readonly Border _captionBar;
    private readonly TextBlock _caption;
    private readonly TextBlock _placeholder;

    private bool _isHover;
    private bool _isActive;

    public IntPtr Handle { get; private set; }
    public bool IsMinimizedState { get; private set; }

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
        _captionBar = new Border { Background = CaptionBg, Child = _caption };
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

    // 優先度：アクティブ ＞ ホバー ＞ 通常
    private void ApplyHighlight()
    {
        if (_isActive)
        {
            BorderBrush = ActiveBorder;
            _captionBar.Background = ActiveCaptionBg;
            _caption.Foreground = ActiveCaptionFg;
        }
        else
        {
            BorderBrush = _isHover ? HoverBorder : TileBorder;
            _captionBar.Background = CaptionBg;
            _caption.Foreground = CaptionFg;
        }
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
