using System.Windows.Threading;

namespace VSCodeLiveTiles.Services;

/// <summary>
/// UI スレッドが詰まっていないかをバックグラウンドから監視して記録する。
///
/// Windows は 5 秒メッセージを処理しないウィンドウをゴースト（白い「応答なし」窓）に置き換える。
/// そこまで行くとユーザーには「フリーズした」としか見えず、原因も残らない。
/// UI スレッドへ定期的に ping を投げ、往復に <see cref="_thresholdMs"/> 以上かかったら
/// 停止中と復帰後の両方を残す。停止中の記録は、原因の呼び出しがまだスタックに居る時刻を示す。
/// </summary>
public sealed class UiThreadWatchdog : IDisposable
{
    private const int DefaultThresholdMs = 1000;
    private const int ProbeIntervalMs = 500;

    private readonly Dispatcher _dispatcher;
    private readonly int _thresholdMs;
    private readonly Timer _timer;

    private long _pingSentAt;        // 0 = 未応答の ping なし。それ以外は送出時刻（TickCount64）
    private volatile bool _stallReported;
    private volatile bool _disposed;

    public UiThreadWatchdog(Dispatcher dispatcher, int thresholdMs = DefaultThresholdMs)
    {
        _dispatcher = dispatcher;
        _thresholdMs = thresholdMs;
        _timer = new Timer(Probe, null, ProbeIntervalMs, ProbeIntervalMs);
    }

    private void Probe(object? _)
    {
        if (_disposed)
            return;

        // 前回の ping がまだ返っていない = UI スレッドは今まさに止まっている
        long outstanding = Interlocked.Read(ref _pingSentAt);
        if (outstanding != 0)
        {
            long stuckFor = Environment.TickCount64 - outstanding;
            if (stuckFor >= _thresholdMs && !_stallReported)
            {
                _stallReported = true;
                Log.Warn($"UI スレッドが {stuckFor} ms 応答していません（停止中）");
            }
            return; // 返るまで ping は重ねない
        }

        long sentAt = Environment.TickCount64;
        Interlocked.Exchange(ref _pingSentAt, sentAt);
        try
        {
            // Input 優先度: 実際のユーザー操作と同じ順番で処理されるので、体感の詰まりを測れる
            _dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                long roundTrip = Environment.TickCount64 - sentAt;
                Interlocked.Exchange(ref _pingSentAt, 0);
                if (roundTrip >= _thresholdMs)
                    Log.Warn($"UI スレッドが {roundTrip} ms 停止していました（復帰）");
                _stallReported = false;
            });
        }
        catch
        {
            // Dispatcher が終了処理に入っている。監視する対象がもう無い
            Interlocked.Exchange(ref _pingSentAt, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Dispose();
    }
}
