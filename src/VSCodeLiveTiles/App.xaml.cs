using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using VSCodeLiveTiles.Services;

namespace VSCodeLiveTiles;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsInstanceLock;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Start();
        InstallExceptionHandlers();

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
        LogEnvironment(monitors);

        var window = new MainWindow(config, monitors);
        window.Show();
    }

    /// <summary>
    /// 例外で黙って消えるのをやめる。UI スレッドの例外は記録した上で握り、常駐を続ける
    /// （1 回の描画・DWM 呼び出しの失敗でウィジェットごと落とす価値はない）。
    /// 他スレッドの例外はプロセスを止められないので、せめて痕跡を残す。
    /// </summary>
    private void InstallExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("未処理例外（プロセスが終了します）", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("バックグラウンドタスクの未観測例外", args.Exception);
            args.SetObserved();
        };
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI スレッドの未処理例外（継続します）", e.Exception);
        e.Handled = true;
    }

    /// <summary>起動時の環境を 1 度だけ残す。後からログを読むとき、再現環境の特定がこれ 1 行で済む。</summary>
    private static void LogEnvironment(MonitorService monitors)
    {
        if (Log.Directory is null)
            return;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        Log.Info($"起動 v{version} / {RuntimeInformation.OSDescription} / {RuntimeInformation.FrameworkDescription} / {RuntimeInformation.ProcessArchitecture}");

        try
        {
            var mons = monitors.GetMonitors();
            Log.Info($"モニター {mons.Count} 枚: " + string.Join(" , ", mons.Select(m =>
                $"[{m.Index}]{(m.IsPrimary ? "primary" : "sub")} {m.Width}x{m.Height} @({m.Left},{m.Top})")));
        }
        catch (Exception ex)
        {
            Log.Error("モニター列挙に失敗", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceLock)
            _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
