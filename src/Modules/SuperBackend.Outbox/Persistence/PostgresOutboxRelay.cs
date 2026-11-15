using Microsoft.EntityFrameworkCore;

namespace SuperBackend.Outbox;

internal sealed class PostgresOutboxRelay(OutboxData data)
{
    public Task<List<bool>> DispatchAsync(int batchSize, CancellationToken ct) =>
        data.Database.SqlQuery<bool>($"""
            WITH batch AS (
                SELECT "Id", "Type", "Payload", "MaxAttempts", "CreatedAt"
                FROM outbox."Messages"
                WHERE "DispatchedAt" IS NULL AND "ConflictAt" IS NULL
                ORDER BY "CreatedAt", "Id"
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
            ),
            jobs AS (
                INSERT INTO jobs."Jobs" AS existing ("Id", "Type", "Payload", "Status", "Attempts", "MaxAttempts", "AvailableAt")
                SELECT "Id", "Type", "Payload", 'pending', 0, "MaxAttempts", now()
                FROM batch
                ORDER BY "CreatedAt", "Id"
                ON CONFLICT ("Id") DO UPDATE SET "Id" = EXCLUDED."Id"
                WHERE existing."Type" = EXCLUDED."Type" AND existing."Payload" = EXCLUDED."Payload"
                    AND existing."MaxAttempts" = EXCLUDED."MaxAttempts"
                RETURNING "Id"
            )
            UPDATE outbox."Messages" AS message
            SET "DispatchedAt" = CASE WHEN jobs."Id" IS NOT NULL THEN now() END,
                "ConflictAt" = CASE WHEN jobs."Id" IS NULL THEN now() END
            FROM batch LEFT JOIN jobs ON batch."Id" = jobs."Id"
            WHERE message."Id" = batch."Id"
            RETURNING message."ConflictAt" IS NOT NULL AS "Value"
            """).ToListAsync(ct);
}
