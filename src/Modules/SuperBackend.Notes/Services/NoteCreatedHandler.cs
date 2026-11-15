using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SuperBackend.Jobs;
using SuperBackend.Outbox;

namespace SuperBackend.Notes;

internal sealed class NoteCreatedHandler(NotesData data, IJobInbox inbox) : IDurableJobHandler
{
    public string Type => NoteCreated.JobType;

    public async Task HandleAsync(JobExecution job, CancellationToken ct)
    {
        var message = job.Payload.Deserialize<NoteCreated>()
            ?? throw new JsonException("Expected a note-created payload.");
        ArgumentException.ThrowIfNullOrWhiteSpace(message.OwnerId);

        await using var transaction = await data.Database.BeginTransactionAsync(ct);
        if (!await inbox.TryRecordAsync(transaction.GetDbTransaction(), Type, job.Id, ct))
            return;

        await data.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notes."NoteCreationStats" AS stats ("OwnerId", "TotalCreated")
            VALUES ({message.OwnerId}, 1)
            ON CONFLICT ("OwnerId") DO UPDATE SET "TotalCreated" = stats."TotalCreated" + 1
            """, ct);
        await transaction.CommitAsync(ct);
    }
}
