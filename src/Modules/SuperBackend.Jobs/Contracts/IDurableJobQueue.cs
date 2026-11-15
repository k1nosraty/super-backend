using System.Text.Json;

namespace SuperBackend.Jobs;

public interface IDurableJobQueue
{
    // The ID is an idempotency key. An existing job keeps its original type and payload.
    Task<bool> EnqueueAsync(Guid id, string type, JsonElement payload, int maxAttempts = 5,
        CancellationToken ct = default);
}

public interface IDurableJobHandler
{
    string Type { get; }
    Task HandleAsync(JobExecution job, CancellationToken ct);
}
