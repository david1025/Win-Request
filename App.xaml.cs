using System;
using System.IO;
using System.Text;
using Microsoft.UI.Xaml;
using WinRequest.Models;
using WinRequest.Services;

namespace WinRequest;

public partial class App : Application
{
    private static System.Threading.Mutex? _singleInstanceMutex;
    private Window? _window;

    public static new App Current => (App)Application.Current;
    public IntPtr MainWindowHandle => _window != null
        ? WinRT.Interop.WindowNative.GetWindowHandle(_window)
        : IntPtr.Zero;

    public void ApplySettings(AppSettings settings)
    {
        if (_window?.Content is FrameworkElement root)
            AppSettingsApplier.ApplyToRoot(root, settings);
    }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            LogCrash(e.Exception, e.Message);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash(e.ExceptionObject as Exception, $"AppDomain: {e.ExceptionObject}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        const string mutexName = "WinRequest_SingleInstance_Global_V1";
        _singleInstanceMutex = new System.Threading.Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            Environment.Exit(0);
            return;
        }

        _window = new MainWindow();
        _window.Activate();
        _window.AppWindow.Show();
    }

    private static void LogCrash(Exception? ex, string message)
    {
        try
        {
            string logPath = Path.Combine(AppContext.BaseDirectory, "crash_log.txt");
            var sb = new StringBuilder();
            sb.AppendLine("==================================================");
            sb.AppendLine($"[Crash Timestamp] {DateTime.Now}");
            sb.AppendLine($"[Message] {message}");
            if (ex != null)
                sb.AppendLine(ex.ToString());
            File.AppendAllText(logPath, sb.ToString());
        }
        catch
        {
        }
    }
}
