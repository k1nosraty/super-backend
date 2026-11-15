namespace SuperBackend.Jobs;

public interface IJobOperations
{
    Task<List<JobSummary>> ListAsync(int offset, CancellationToken ct);
    Task<bool> RetryAsync(Guid id, CancellationToken ct);
}
