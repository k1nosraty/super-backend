using Npgsql;

namespace SuperBackend.Api;

internal static class RetentionMaintenance
{
    internal static async Task RunAsync(string connectionString, FeatureProfile features, int finishedDays, int batchSize)
    {
        if (finishedDays is < 30 or > 3650 || batchSize is < 1 or > 10000)
            throw new ArgumentException("Retention requires 30–3650 days and batches of 1–10000 rows.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var expiredSessions = await ExecuteAsync("""
            DELETE FROM identity."Sessions" WHERE "Id" IN (
                SELECT "Id" FROM identity."Sessions" WHERE "ExpiresAt" <= now()
                ORDER BY "ExpiresAt", "Id" LIMIT @batch FOR UPDATE SKIP LOCKED);
            """);
        var expiredHandoffs = await ExecuteAsync("""
            DELETE FROM identity."FederationHandoffs" WHERE "Id" IN (
                SELECT "Id" FROM identity."FederationHandoffs" WHERE "ExpiresAt" <= now()
                ORDER BY "ExpiresAt", "Id" LIMIT @batch FOR UPDATE SKIP LOCKED);
            """);
        if (features.Notes)
            await ExecuteAsync("""
                DELETE FROM notes."NoteCreateReceipts" WHERE ("OwnerId", "Key") IN (
                    SELECT "OwnerId", "Key" FROM notes."NoteCreateReceipts" WHERE "CreatedAt" < now() - @retention
                    ORDER BY "CreatedAt", "OwnerId", "Key" LIMIT @batch FOR UPDATE SKIP LOCKED);
                """);

        // Keep job IDs and inbox receipts together. Only remove fully settled linked pairs.
        var finishedJobs = 0;
        if (features.Outbox)
        {
            finishedJobs = await ExecuteAsync("""
                WITH settled AS (
                    SELECT jobs."Id" FROM jobs."Jobs" AS jobs
                    JOIN outbox."Messages" AS messages ON messages."Id" = jobs."Id"
                    WHERE jobs."Status" = 'succeeded' AND jobs."FinishedAt" < now() - @retention
                      AND messages."DispatchedAt" < now() - @retention
                      AND NOT EXISTS (SELECT 1 FROM outbox."InboxReceipts" receipts
                          WHERE receipts."MessageId" = jobs."Id" AND receipts."ProcessedAt" >= now() - @retention)
                    ORDER BY jobs."FinishedAt", jobs."Id" LIMIT @batch FOR UPDATE OF jobs, messages SKIP LOCKED
                ), receipts AS (
                    DELETE FROM outbox."InboxReceipts" WHERE "MessageId" IN (SELECT "Id" FROM settled)
                ), messages AS (
                    DELETE FROM outbox."Messages" WHERE "Id" IN (SELECT "Id" FROM settled)
                )
                DELETE FROM jobs."Jobs" WHERE "Id" IN (SELECT "Id" FROM settled);
                """);
        }

        await transaction.CommitAsync();
        Console.WriteLine($"Expired sessions: {expiredSessions}, expired handoffs: {expiredHandoffs}, settled jobs: {finishedJobs}.");

        async Task<int> ExecuteAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("batch", batchSize);
            command.Parameters.AddWithValue("retention", TimeSpan.FromDays(finishedDays));
            return await command.ExecuteNonQueryAsync();
        }
    }
}
