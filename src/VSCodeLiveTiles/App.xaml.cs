using System.Windows;
using VSCodeLiveTiles.Services;

namespace VSCodeLiveTiles;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 多重起動防止（サムネイル二重登録を避ける）
        _singleInstance = new Mutex(initiallyOwned: true, "VSCodeLiveTiles_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("VSCode ライブタイルは既に起動しています。", "VSCodeLiveTiles",
                MessageBoxButton.OK, MessageBoxImage.Information);
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
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
