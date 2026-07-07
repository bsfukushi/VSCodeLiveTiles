using System.Windows.Threading;
using VSCodeLiveTiles.Interop;

namespace VSCodeLiveTiles.Services;

/// <summary>
/// 対象プロセス（既定は Code.exe）の可視トップレベルウィンドウを監視する。
/// WinEvent フック + 保険の定期ポーリングで差分を検知し、変化時のみ <see cref="Updated"/> を発火。
/// </summary>
public sealed class WindowTracker : IDisposable
{
    private readonly IReadOnlySet<string> _processNames;
    private readonly Func<IntPtr> _selfHandleProvider;
    private readonly WinEventHook _hook;
    private readonly DispatcherTimer? _pollTimer;

    private string _lastSignature = "";
    private bool _disposed;

    /// <summary>ウィンドウ集合が変化したときに UI スレッドで発火。最新スナップショットを渡す。</summary>
    public event Action<IReadOnlyList<NativeWindows.WindowInfo>>? Updated;

    /// <summary>前面ウィンドウが変わったときに UI スレッドで発火（ハンドルを渡す）。</summary>
    public event Action<IntPtr>? ActiveWindowChanged;

    public WindowTracker(AppConfig config, Func<IntPtr> selfHandleProvider)
    {
        _processNames = new HashSet<string>(config.TargetProcessNames, StringComparer.OrdinalIgnoreCase);
        _selfHandleProvider = selfHandleProvider;

        _hook = new WinEventHook();
        _hook.Changed += Rescan;
        _hook.ForegroundChanged += h => ActiveWindowChanged?.Invoke(h);

        if (config.RefreshIntervalMs > 0)
        {
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(config.RefreshIntervalMs) };
            _pollTimer.Tick += (_, _) => Rescan();
        }
    }

    /// <summary>初回スキャンして監視を開始する。</summary>
    public void Start()
    {
        _pollTimer?.Start();
        Rescan(force: true);
    }

    public IReadOnlyList<NativeWindows.WindowInfo> Snapshot()
        => NativeWindows.EnumerateWindows(_processNames, _selfHandleProvider());

    private void Rescan() => Rescan(false);

    private void Rescan(bool force)
    {
        if (_disposed)
            return;

        var windows = Snapshot();
        // handle + タイトル + 最小化状態 でシグネチャ化。変化がなければ再構築しない。
        var sig = string.Join("|", windows
            .OrderBy(w => (long)w.Handle)
            .Select(w => $"{w.Handle:X}:{w.IsMinimized}:{w.Title}"));

        if (!force && sig == _lastSignature)
            return;
        _lastSignature = sig;
        Updated?.Invoke(windows);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pollTimer?.Stop();
        _hook.Changed -= Rescan;
        _hook.Dispose();
    }
}
