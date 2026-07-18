using System.Windows.Threading;
using VSCodeLiveTiles.Interop;

namespace VSCodeLiveTiles.Services;

/// <summary>
/// UI スレッドが詰まっていないかをバックグラウンドから監視して記録する。
///
/// Windows は 5 秒メッセージを処理しないウィンドウをゴースト（白い「応答なし」窓）に置き換える。
/// そこまで行くとユーザーには「フリーズした」としか見えず、原因も残らない。
///
/// 詰まりには原因の異なる 2 種類があり、直す場所が正反対になる。
/// 見分けるため、優先度の違う 2 本の ping を投げる:
/// - <b>Send</b> が返らない → スレッドがネイティブ呼び出しの中で本当にブロックしている。
///   その呼び出しを別スレッドへ逃がすしかない
/// - <b>Send は返るが Input が遅れる</b> → Dispatcher のキューが高優先度の処理で溢れ、
///   入力処理が飢餓を起こしている。流量を絞る（デバウンス・間引き）話になる
///
/// 両方が同時に記録されたときは前者（ブロック）。Send が止まれば Input も必ず止まるため。
///
/// 時間は <see cref="NativeWindows.UnbiasedTickMs"/>（サスペンド時間を含まない）で測る。
/// TickCount64 で測るとスリープをまたいだ ping が「90 万 ms 停止」のような偽の巨大値になる
/// （2026-07-15 のログで実際に発生）。
/// </summary>
public sealed class UiThreadWatchdog : IDisposable
{
    private const int DefaultThresholdMs = 1000;
    private const int ProbeIntervalMs = 500;

    private readonly Timer _timer;
    private readonly Ping _blocked;
    private readonly Ping _starved;
    private volatile bool _disposed;

    public UiThreadWatchdog(Dispatcher dispatcher, int thresholdMs = DefaultThresholdMs)
    {
        _blocked = new Ping(dispatcher, DispatcherPriority.Send, "UI スレッド[Send]", thresholdMs);
        _starved = new Ping(dispatcher, DispatcherPriority.Input, "入力処理[Input]", thresholdMs);
        _timer = new Timer(_ => Probe(), null, ProbeIntervalMs, ProbeIntervalMs);
    }

    private void Probe()
    {
        if (_disposed)
            return;
        _blocked.Probe();
        _starved.Probe();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Dispose();
    }

    /// <summary>ひとつの優先度に対する ping の往復計測。前の ping が返るまで次は投げない。</summary>
    private sealed class Ping
    {
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherPriority _priority;
        private readonly string _label;
        private readonly int _thresholdMs;

        private long _sentAt;              // 0 = 未応答の ping なし。それ以外は送出時刻（UnbiasedTickMs）
        private volatile bool _reported;   // この停止について「継続中」を既に記録したか

        public Ping(Dispatcher dispatcher, DispatcherPriority priority, string label, int thresholdMs)
        {
            _dispatcher = dispatcher;
            _priority = priority;
            _label = label;
            _thresholdMs = thresholdMs;
        }

        public void Probe()
        {
            long outstanding = Interlocked.Read(ref _sentAt);
            if (outstanding != 0)
            {
                // 前回の ping がまだ返っていない = 今まさに詰まっている。
                // 原因の呼び出しがスタックに居る時刻を残せるのはここだけ
                long stuckFor = NativeWindows.UnbiasedTickMs() - outstanding;
                if (stuckFor >= _thresholdMs && !_reported)
                {
                    _reported = true;
                    Log.Warn($"{_label} が {stuckFor} ms 応答していません（継続中）");
                }
                return;
            }

            long sentAt = NativeWindows.UnbiasedTickMs();
            Interlocked.Exchange(ref _sentAt, sentAt);
            try
            {
                _dispatcher.BeginInvoke(_priority, () =>
                {
                    long roundTrip = NativeWindows.UnbiasedTickMs() - sentAt;
                    Interlocked.Exchange(ref _sentAt, 0);
                    if (roundTrip >= _thresholdMs)
                        Log.Warn($"{_label} が {roundTrip} ms 停止していました（復帰）");
                    _reported = false;
                });
            }
            catch
            {
                // Dispatcher が終了処理に入っている。監視する対象がもう無い
                Interlocked.Exchange(ref _sentAt, 0);
            }
        }
    }
}
