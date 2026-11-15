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
                var results = await scope.ServiceProvider.GetRequiredService<PostgresOutboxRelay>()
                    .DispatchAsync(settings.BatchSize, stoppingToken);
                var conflicts = results.Count(conflict => conflict);
                if (conflicts != 0)
                    logger.LogWarning("{Count} outbox messages conflict with existing job IDs.", conflicts);
                if (results.Count == settings.BatchSize)
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Outbox dispatch failed ({ErrorType}); pending messages will be retried.",
                    exception.GetType().Name);
            }

            await Task.Delay(settings.Poll, stoppingToken);
        }
    }
}
