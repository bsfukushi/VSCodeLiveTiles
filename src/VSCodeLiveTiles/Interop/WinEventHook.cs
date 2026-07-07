using System.Runtime.InteropServices;

namespace VSCodeLiveTiles.Interop;

/// <summary>
/// SetWinEventHook のラッパ。ウィンドウの生成/破棄/表示/フォーカス/位置変化を購読し、
/// UI スレッドで <see cref="Changed"/> を発火する（イベントは合体・デバウンスして通知）。
/// </summary>
internal sealed class WinEventHook : IDisposable
{
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;

    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private const int OBJID_WINDOW = 0;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // GC で回収されないよう保持
    private readonly WinEventDelegate _proc;
    private readonly List<IntPtr> _hooks = new();
    private readonly System.Windows.Threading.DispatcherTimer _debounce;
    private bool _disposed;

    /// <summary>差分が発生したことを UI スレッドで通知する（デバウンス済み）。</summary>
    public event Action? Changed;

    public WinEventHook()
    {
        _proc = OnWinEvent;

        _debounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Changed?.Invoke();
        };

        // 生成〜表示系（連続レンジ）
        Hook(EVENT_OBJECT_CREATE, EVENT_OBJECT_NAMECHANGE);
        // フォアグラウンド／最小化系
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
    }

    private void Hook(uint min, uint max)
    {
        var h = SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (h != IntPtr.Zero)
            _hooks.Add(h);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // ウィンドウ本体（子コントロールでない）だけを対象にノイズを削る
        if (idObject != OBJID_WINDOW)
            return;

        // OUTOFCONTEXT のコールバックは通知元スレッドで来る。UI スレッドでデバウンス起動。
        _debounce.Dispatcher.BeginInvoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _debounce.Stop();
        foreach (var h in _hooks)
            UnhookWinEvent(h);
        _hooks.Clear();
    }
}
