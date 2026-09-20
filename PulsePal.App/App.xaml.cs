using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PulsePal.App.Services;
using PulsePal.Infrastructure;

namespace PulsePal.App;

public partial class App : Application
{
    private IHost? _host;
    private AppController? _controller;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            AppLog.Write("Unhandled UI error", args.Exception);
            if (_controller is not null)
            {
                args.Handled = true;
                _controller.ReportError("Unexpected UI error", args.Exception);
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Write("Fatal process error", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("Unobserved task error", args.Exception);
            args.SetObserved();
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(dispatcher);
                    services.AddSingleton<DemoEngine>();
                    services.AddSingleton<JsonStateStore>();
                    services.AddSingleton<AppController>();
                    services.AddHostedService<SamplingService>();
                }).Build();
            _controller = _host.Services.GetRequiredService<AppController>();
            _controller.ExitRequested += ExitAsync;
            await _controller.InitializeAsync();
            await _host.StartAsync();
            var arguments = Environment.GetCommandLineArgs();
            if (arguments.Contains("--demo-controls", StringComparer.OrdinalIgnoreCase))
                _controller.ShowControls();
            if (arguments.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
                await _controller.RunSmokeTestAsync(arguments.Contains("--smoke-test-full", StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            AppLog.Write("Startup failed", exception);
            Environment.ExitCode = 1;
            if (Environment.GetCommandLineArgs().Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.CreateDirectory(AppLog.DirectoryPath);
                    await File.WriteAllTextAsync(Path.Combine(AppLog.DirectoryPath, "smoke-test.json"),
                        System.Text.Json.JsonSerializer.Serialize(new { UiInitialized = false, Error = exception.ToString(), Timestamp = DateTimeOffset.Now }));
                }
                catch (Exception reportError) { AppLog.Write("Smoke failure report failed", reportError); }
                if (_controller?.HasWindow == true) _controller.RequestExit();
                else Exit();
                return;
            }
            if (_controller?.HasWindow == true)
                _controller.ReportError("Startup failed", exception);
            else
            {
                NativeMessage.Show("PulsePal could not start.\n\n" + exception.Message + "\n\nDiagnostics: " + AppLog.DirectoryPath);
                Exit();
            }
        }
    }

    private async void ExitAsync()
    {
        try
        {
            if (_host is not null)
            {
                try { await _host.StopAsync(TimeSpan.FromSeconds(8)); }
                catch (Exception exception) { AppLog.Write("Hosted sampling stop failed", exception); }
            }
            if (_controller is not null)
                await _controller.ShutdownAsync();
        }
        catch (Exception exception)
        {
            AppLog.Write("Shutdown failed", exception);
            NativeMessage.Show("PulsePal could not save or finish shutting down cleanly.\n\n" + exception.Message);
        }
        finally
        {
            try { _controller?.Dispose(); }
            catch (Exception exception) { AppLog.Write("UI resource cleanup failed", exception); }
            try { _host?.Dispose(); }
            catch (Exception exception) { AppLog.Write("Host cleanup failed", exception); }
            finally { Exit(); }
        }
    }
}

internal static class NativeMessage
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
    public static void Show(string text) => MessageBoxW(0, text, "PulsePal", 0x10);
}
