using System.Data.Common;

namespace SuperBackend.Outbox;

public interface IJobInbox
{
    Task<bool> TryRecordAsync(DbTransaction transaction, string consumer, Guid messageId, CancellationToken ct = default);
}
