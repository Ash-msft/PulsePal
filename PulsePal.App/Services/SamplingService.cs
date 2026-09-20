using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using PulsePal.Infrastructure;

namespace PulsePal.App.Services;

public sealed class SamplingService(DemoEngine engine, AppController controller, DispatcherQueue dispatcher) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var snapshot = await engine.TickAsync(DateTimeOffset.Now, stoppingToken);
                    if (!dispatcher.TryEnqueue(() => controller.AcceptSnapshot(snapshot)))
                        AppLog.Write("Sampling dispatch rejected", new InvalidOperationException("UI dispatcher is shutting down."));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    AppLog.Write("Sampling failed", exception);
                    dispatcher.TryEnqueue(() => controller.ReportError("Synthetic sampling failed", exception));
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
