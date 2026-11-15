using System.Data;
using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using SuperBackend.Jobs;

namespace SuperBackend.Outbox;

internal sealed class TransactionalMessages : ITransactionalOutbox, IJobInbox
{
    public async Task RecordAsync(DbTransaction transaction, JobSubmission message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var command = CreateCommand(transaction, """
            INSERT INTO outbox."Messages" AS existing ("Id", "Type", "Payload", "MaxAttempts", "CreatedAt")
            VALUES (@id, @type, @payload, @attempts, now())
            ON CONFLICT ("Id") DO UPDATE SET "Id" = EXCLUDED."Id"
            WHERE existing."Type" = EXCLUDED."Type" AND existing."Payload" = EXCLUDED."Payload"
                AND existing."MaxAttempts" = EXCLUDED."MaxAttempts"
            """);
        command.Parameters.AddWithValue("id", message.Id);
        command.Parameters.AddWithValue("type", message.Type);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, message.PayloadJson);
        command.Parameters.AddWithValue("attempts", message.MaxAttempts);
        if (await command.ExecuteNonQueryAsync(ct) == 0)
            throw new InvalidOperationException("The outbox message ID already identifies a different submission.");
    }

    public async Task<bool> TryRecordAsync(DbTransaction transaction, string consumer, Guid messageId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(consumer.Length, 100);
        ArgumentOutOfRangeException.ThrowIfEqual(messageId, Guid.Empty);
        await using var command = CreateCommand(transaction, """
            INSERT INTO outbox."InboxReceipts" ("Consumer", "MessageId", "ProcessedAt")
            VALUES (@consumer, @id, now())
            ON CONFLICT ("Consumer", "MessageId") DO NOTHING
            """);
        command.Parameters.AddWithValue("consumer", consumer);
        command.Parameters.AddWithValue("id", messageId);
        return await command.ExecuteNonQueryAsync(ct) != 0;
    }

    private static NpgsqlCommand CreateCommand(DbTransaction transaction, string sql)
    {
        if (transaction is not NpgsqlTransaction { Connection: { State: ConnectionState.Open } connection } postgres)
            throw new ArgumentException("An active PostgreSQL transaction is required.", nameof(transaction));

        return new NpgsqlCommand(sql, connection, postgres);
    }
}
