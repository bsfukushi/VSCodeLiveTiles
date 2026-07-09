using System.Windows;
using VSCodeLiveTiles.Services;

namespace VSCodeLiveTiles;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsInstanceLock;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 多重起動防止（サムネイル二重登録を避ける）。
        // 所有権は WaitOne で取る。initiallyOwned: true では所有権を得られなかった
        // 2 つ目のインスタンスでも OnExit が ReleaseMutex を呼び、
        // ApplicationException でクラッシュしていた（v0.7.2 で修正）
        _singleInstance = new Mutex(initiallyOwned: false, "VSCodeLiveTiles_SingleInstance");
        try
        {
            _ownsInstanceLock = _singleInstance.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsInstanceLock = true; // 前のインスタンスが解放せずに落ちた。所有権はこちらに移っている
        }

        if (!_ownsInstanceLock)
        {
            // 既にウィジェットが画面に出ているので黙って終了する（ダイアログは出さない）
            Shutdown();
            return;
        }

        var config = AppConfig.Load();
        var monitors = new MonitorService();

        var window = new MainWindow(config, monitors);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceLock)
            _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
