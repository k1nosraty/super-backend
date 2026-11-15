namespace SuperBackend.Jobs;

public interface IJobOperations
{
    Task<List<JobSummary>> ListAsync(int offset, Guid? id, string? status, DateTimeOffset? before, CancellationToken ct);
    Task<bool> RetryAsync(Guid id, string actor, string reason, CancellationToken ct);
}
