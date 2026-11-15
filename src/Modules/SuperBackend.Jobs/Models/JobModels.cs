using System.Text.Json;

namespace SuperBackend.Jobs;

public sealed record JobExecution(Guid Id, JsonElement Payload, int Attempt);
public sealed record JobSummary(Guid Id, string Type, string Status, int Attempts, int MaxAttempts,
    DateTimeOffset AvailableAt, DateTimeOffset? FinishedAt, string? LastError);

internal sealed class Job
{
    public Guid Id { get; init; }
    public required string Type { get; init; }
    public required string Payload { get; init; }
    public required string Status { get; init; }
    public int Attempts { get; init; }
    public int MaxAttempts { get; init; }
    public DateTimeOffset AvailableAt { get; init; }
    public DateTimeOffset? LeaseUntil { get; init; }
    public Guid? LeaseId { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? LastError { get; init; }
}

internal sealed record Lease(Guid Id, string Type, string Payload, Guid LeaseId, int Attempts);
internal sealed record QueueSettings(TimeSpan Poll, TimeSpan Lease, TimeSpan Timeout, TimeSpan Retry);
