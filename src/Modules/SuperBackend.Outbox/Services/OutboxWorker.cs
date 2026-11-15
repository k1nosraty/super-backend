using System.Diagnostics;
using SuperBackend.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SuperBackend.Outbox;

internal sealed class OutboxWorker(IServiceScopeFactory scopes, RelaySettings settings, ILogger<OutboxWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var started = Stopwatch.GetTimestamp();
                var results = await scope.ServiceProvider.GetRequiredService<PostgresOutboxRelay>()
                    .DispatchAsync(settings.BatchSize, stoppingToken);
                var conflicts = results.Count(conflict => conflict);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
                if (conflicts != 0)
                {
                    OperationsTelemetry.Record("outbox", "conflict", elapsed, conflicts);
                    logger.LogWarning("{Count} outbox messages conflict with existing job IDs.", conflicts);
                }
                if (results.Count > conflicts)
                    OperationsTelemetry.Record("outbox", "dispatched", elapsed, results.Count - conflicts);
                if (results.Count == settings.BatchSize)
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                OperationsTelemetry.Record("outbox", "store-error", 0);
                logger.LogWarning("Outbox dispatch failed ({ErrorType}); pending messages will be retried.",
                    exception.GetType().Name);
            }

            await Task.Delay(settings.Poll, stoppingToken);
        }
    }
}
