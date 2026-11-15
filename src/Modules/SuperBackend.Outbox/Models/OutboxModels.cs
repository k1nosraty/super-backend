namespace SuperBackend.Outbox;

internal sealed class OutboxMessage
{
    public Guid Id { get; init; }
    public required string Type { get; init; }
    public required string Payload { get; init; }
    public int MaxAttempts { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DispatchedAt { get; init; }
    public DateTimeOffset? ConflictAt { get; init; }
}

internal sealed record InboxReceipt(string Consumer, Guid MessageId, DateTimeOffset ProcessedAt);
internal sealed record RelaySettings(int BatchSize, TimeSpan Poll);
