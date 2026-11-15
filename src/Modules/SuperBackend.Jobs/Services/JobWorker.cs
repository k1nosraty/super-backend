using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SuperBackend.Jobs;

internal sealed class JobWorker(IServiceScopeFactory scopes, QueueSettings settings, ILogger<JobWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await ProcessNextAsync(stoppingToken))
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Job store operation failed ({ErrorType}); polling will resume.",
                    exception.GetType().Name);
            }

            await Task.Delay(settings.Poll, stoppingToken);
        }
    }

    private async Task<bool> ProcessNextAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<PostgresJobQueue>();
        var lease = await queue.ClaimAsync(settings.Lease, stoppingToken);
        if (lease is null)
            return false;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(settings.Timeout);
        string? error = null;
        try
        {
            var handler = scope.ServiceProvider.GetServices<IDurableJobHandler>()
                .SingleOrDefault(item => item.Type == lease.Type);
            if (handler is null)
            {
                error = "missing-handler";
            }
            else
            {
                using var payload = JsonDocument.Parse(lease.Payload);
                await handler.HandleAsync(new JobExecution(lease.Id, payload.RootElement, lease.Attempts), deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            stoppingToken.ThrowIfCancellationRequested();
            error = "deadline-exceeded";
        }
        catch (Exception exception)
        {
            logger.LogWarning("Job {JobId} attempt {Attempt} failed ({ErrorType}).",
                lease.Id, lease.Attempts, exception.GetType().Name);
            error = "handler-failed";
        }

        stoppingToken.ThrowIfCancellationRequested();
        var backoff = TimeSpan.FromSeconds(Math.Min(300, settings.Retry.TotalSeconds * Math.Pow(2, lease.Attempts - 1)));
        await queue.FinishAsync(lease, error, backoff, stoppingToken);
        return true;
    }
}
