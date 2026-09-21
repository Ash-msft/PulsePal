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
    private readonly string[] _arguments = Environment.GetCommandLineArgs();
    private bool IsPresentationSmoke => _arguments.Contains("--smoke-test-presentation", StringComparer.OrdinalIgnoreCase);
    private bool IsSmoke => IsPresentationSmoke || _arguments.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase)
        || _arguments.Contains("--smoke-test-full", StringComparer.OrdinalIgnoreCase);
    private string? _smokeDirectory;

    public App()
    {
        if (IsSmoke)
        {
            _smokeDirectory = Path.GetFullPath(Path.Combine("artifacts", "presentation-smoke-" + Guid.NewGuid().ToString("N")));
            AppLog.ConfigureDirectory(_smokeDirectory);
        }
        try { InitializeComponent(); }
        catch (Exception exception)
        {
            if (IsSmoke) WriteSmokeFailure(exception);
            throw;
        }
        UnhandledException += (_, args) =>
        {
            AppLog.Write("Unhandled UI error", args.Exception);
            if (IsSmoke)
            {
                WriteSmokeFailure(args.Exception);
                args.Handled = true;
                if (_controller is not null) _controller.RequestExit();
                else Exit();
                return;
            }
            if (_controller is not null)
            {
                args.Handled = true;
                _controller.ReportError("Unexpected UI error", args.Exception);
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject.ToString());
            AppLog.Write("Fatal process error", exception);
            if (IsSmoke) WriteSmokeFailure(exception);
        };
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
            var logDirectory = ReadPathOption("--log-directory");
            var statePath = ReadPathOption("--state-path");
            if (IsSmoke)
            {
                // A smoke run may create an explicitly named state file, but never load existing user state.
                statePath = Path.GetFullPath(statePath ?? Path.Combine(_smokeDirectory!, "state.json"));
                var ordinaryState = new JsonStateStore().FilePath;
                if (string.Equals(statePath, ordinaryState, StringComparison.OrdinalIgnoreCase) || File.Exists(statePath))
                    throw new ArgumentException("Smoke --state-path must name a new, isolated state file.");
                if (logDirectory is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(logDirectory)),
                    Path.GetDirectoryName(ordinaryState), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Smoke logs cannot use the ordinary user-state directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                using var state = new FileStream(statePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                state.Write("{}"u8);
            }
            if (logDirectory is not null) AppLog.ConfigureDirectory(logDirectory);
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(dispatcher);
                    services.AddSingleton<DemoEngine>();
                    services.AddSingleton(statePath is null ? new JsonStateStore() : new JsonStateStore(statePath));
                    services.AddSingleton<AppController>();
                    services.AddHostedService<SamplingService>();
                }).Build();
            _controller = _host.Services.GetRequiredService<AppController>();
            _controller.ExitRequested += ExitAsync;
            await _controller.InitializeAsync();
            await _host.StartAsync();
            if (_arguments.Contains("--demo-controls", StringComparer.OrdinalIgnoreCase))
                _controller.ShowControls();
            if (IsPresentationSmoke)
                await _controller.RunPresentationSmokeTestAsync();
            else if (IsSmoke)
                await _controller.RunSmokeTestAsync(_arguments.Contains("--smoke-test-full", StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            AppLog.Write("Startup failed", exception);
            Environment.ExitCode = 1;
            if (IsSmoke)
            {
                WriteSmokeFailure(exception);
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

    private void WriteSmokeFailure(Exception exception)
    {
        Environment.ExitCode = 1;
        try
        {
            Directory.CreateDirectory(AppLog.DirectoryPath);
            File.WriteAllText(Path.Combine(AppLog.DirectoryPath,
                IsPresentationSmoke ? "smoke-test-presentation.json" : "smoke-test.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    UiInitialized = _controller?.HasWindow == true, Passed = false, PresentationTest = IsPresentationSmoke,
                    FullTest = _arguments.Contains("--smoke-test-full", StringComparer.OrdinalIgnoreCase),
                    Error = exception.ToString(), Timestamp = DateTimeOffset.Now,
                    LogDirectory = AppLog.DirectoryPath,
                    Flags = _arguments.Where(argument => argument.StartsWith("--", StringComparison.Ordinal)).ToArray(),
                    Checks = new Dictionary<string, bool> { ["NoUnhandledException"] = false }
                }));
        }
        catch (Exception reportError) { AppLog.Write("Smoke failure report failed", reportError); }
    }

    private string? ReadPathOption(string option)
    {
        var matches = _arguments.Select((value, index) => (value, index))
            .Where(item => string.Equals(item.value, option, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return null;
        var index = matches[0].index;
        if (matches.Length != 1 || index + 1 >= _arguments.Length ||
            string.IsNullOrWhiteSpace(_arguments[index + 1]) || _arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException(option + " requires one path argument.");
        return Path.GetFullPath(_arguments[index + 1]);
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
            Environment.ExitCode = 1;
            if (!IsSmoke)
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
