using System.Diagnostics;
using SuperBackend.Platform;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SuperBackend.Jobs;

internal sealed class JobWorker(IServiceScopeFactory scopes, QueueSettings settings, ILogger<JobWorker> logger)
    : BackgroundService
{
    private long _lastLeaseSweep;

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
                OperationsTelemetry.Record("jobs", "store-error", 0);
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
        if (_lastLeaseSweep == 0 || Stopwatch.GetElapsedTime(_lastLeaseSweep) >= TimeSpan.FromSeconds(30))
        {
            var expired = await queue.ExpireExhaustedLeasesAsync(stoppingToken);
            if (expired < 100)
                _lastLeaseSweep = Stopwatch.GetTimestamp();
        }

        var handlers = scope.ServiceProvider.GetServices<IDurableJobHandler>()
            .ToDictionary(handler => handler.Type, StringComparer.Ordinal);
        if (handlers.Count == 0)
            return false;
        var lease = await queue.ClaimAsync(settings.Lease, handlers.Keys.ToArray(), stoppingToken);
        if (lease is null)
            return false;

        var started = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(settings.Timeout);
        string? error = null;
        try
        {
            using var payload = JsonDocument.Parse(lease.Payload);
            await handlers[lease.Type].HandleAsync(new JobExecution(lease.Id, payload.RootElement, lease.Attempts), deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
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
        var changed = await queue.FinishAsync(lease, error, backoff, stoppingToken);
        OperationsTelemetry.Record("jobs", changed == 0 ? "lease-lost" : error ?? "succeeded",
            Stopwatch.GetElapsedTime(started).TotalSeconds);
        return true;
    }
}
