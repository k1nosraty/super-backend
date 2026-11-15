using System.Text;
using System.Text.Json;

namespace SuperBackend.Jobs;

public sealed class JobSubmission
{
    public JobSubmission(Guid id, string type, JsonElement payload, int maxAttempts = 5)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(type.Length, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAttempts, 20);
        var json = payload.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > 65536)
            throw new ArgumentException("The payload exceeds 64 KiB.", nameof(payload));

        Id = id;
        Type = type;
        PayloadJson = json;
        MaxAttempts = maxAttempts;
    }

    public Guid Id { get; }
    public string Type { get; }
    public string PayloadJson { get; }
    public int MaxAttempts { get; }
}
