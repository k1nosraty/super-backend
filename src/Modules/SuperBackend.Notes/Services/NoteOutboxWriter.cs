using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SuperBackend.Jobs;
using SuperBackend.Outbox;

namespace SuperBackend.Notes;

internal sealed class NoteOutboxWriter(NotesData data, ITransactionalOutbox outbox)
{
    public async Task SaveCreatedAsync(Note note, CancellationToken ct)
    {
        await using var transaction = await data.Database.BeginTransactionAsync(ct);
        await data.SaveChangesAsync(ct);
        await outbox.RecordAsync(transaction.GetDbTransaction(),
            new JobSubmission(note.Id, NoteCreated.JobType, JsonSerializer.SerializeToElement(new NoteCreated(note.OwnerId))), ct);
        await transaction.CommitAsync(ct);
    }
}
