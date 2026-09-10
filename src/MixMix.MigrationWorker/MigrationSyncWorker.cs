using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MixMix.MigrationWorker.Configuration;
using MixMix.MigrationWorker.Databricks;
using MixMix.MigrationWorker.Sync;

namespace MixMix.MigrationWorker;

public sealed class MigrationSyncWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MigrationOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<MigrationSyncWorker> logger) : BackgroundService
{
    private readonly MigrationOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.RunOnce)
        {
            await RunPassAsync(stoppingToken).ConfigureAwait(false);
            lifetime.StopApplication();
            return;
        }

        logger.LogInformation("Sync worker started; a pass will run every {Interval}.", _options.Interval);

        using var timer = new PeriodicTimer(_options.Interval);

        do
        {
            await RunPassAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task RunPassAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();

            var summary = await runner.RunAsync(stoppingToken).ConfigureAwait(false);

            if (!summary.Succeeded)
            {
                // Surfaces the failure to whatever scheduled the worker, without tearing down a long-lived host.
                Environment.ExitCode = 1;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Sync pass cancelled because the host is shutting down.");
        }
        catch (Exception exception) when (exception is MigrationConfigurationException or DatabricksFilesException)
        {
            // These name the setting to change, so the message alone is more useful than a stack trace.
            Environment.ExitCode = 1;
            logger.LogError("{Message}", exception.Message);
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            logger.LogError(exception, "The sync pass failed before any table could be processed.");
        }
    }
}
