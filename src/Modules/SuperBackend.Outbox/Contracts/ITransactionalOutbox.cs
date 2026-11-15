using System.Data.Common;
using SuperBackend.Jobs;

namespace SuperBackend.Outbox;

public interface ITransactionalOutbox
{
    Task RecordAsync(DbTransaction transaction, JobSubmission message, CancellationToken ct = default);
}
