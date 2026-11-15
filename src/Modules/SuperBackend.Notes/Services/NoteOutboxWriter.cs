using System.Text.Json;
using System.Data.Common;
using SuperBackend.Jobs;
using SuperBackend.Outbox;

namespace SuperBackend.Notes;

internal sealed class NoteOutboxWriter(ITransactionalOutbox outbox)
{
    public Task RecordCreatedAsync(Note note, DbTransaction transaction, CancellationToken ct) =>
        outbox.RecordAsync(transaction,
            new JobSubmission(note.Id, NoteCreated.JobType, JsonSerializer.SerializeToElement(new NoteCreated(note.OwnerId))), ct);
}
