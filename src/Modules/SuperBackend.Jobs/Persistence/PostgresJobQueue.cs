using SuperBackend.Platform;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SuperBackend.Jobs;

internal sealed class PostgresJobQueue(JobsData data) : IDurableJobQueue, IJobOperations
{
    public async Task<bool> EnqueueAsync(Guid id, string type, JsonElement payload, int maxAttempts = 5,
        CancellationToken ct = default)
    {
        var submission = new JobSubmission(id, type, payload, maxAttempts);

        return await data.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO jobs."Jobs" ("Id", "Type", "Payload", "Status", "Attempts", "MaxAttempts", "AvailableAt")
            VALUES ({id}, {type}, CAST({submission.PayloadJson} AS jsonb), 'pending', 0, {maxAttempts}, now())
            ON CONFLICT ("Id") DO NOTHING
            """, ct) != 0;
    }

    public Task<int> ExpireExhaustedLeasesAsync(CancellationToken ct) =>
        data.Database.ExecuteSqlRawAsync("""
            WITH exhausted AS (
                SELECT "Id" FROM jobs."Jobs"
                WHERE "Status" = 'running' AND "LeaseUntil" <= now() AND "Attempts" >= "MaxAttempts"
                ORDER BY "LeaseUntil", "Id"
                FOR UPDATE SKIP LOCKED
                LIMIT 100
            )
            UPDATE jobs."Jobs" AS job
            SET "Status" = 'dead', "FinishedAt" = now(), "LeaseId" = NULL, "LeaseUntil" = NULL,
                "LastError" = 'lease-expired'
            FROM exhausted
            WHERE job."Id" = exhausted."Id"
            """, ct);

    public async Task<Lease?> ClaimAsync(TimeSpan duration, string[] supportedTypes, CancellationToken ct)
    {
        var leaseId = Guid.NewGuid();
        var claimed = await data.Database.SqlQuery<Lease>($"""
            WITH candidate AS (
                SELECT "Id"
                FROM jobs."Jobs"
                WHERE "Type" = ANY({supportedTypes}) AND "Attempts" < "MaxAttempts"
                    AND (("Status" = 'pending' AND "AvailableAt" <= now())
                        OR ("Status" = 'running' AND "LeaseUntil" <= now()))
                ORDER BY "AvailableAt", "Id"
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE jobs."Jobs" AS job
            SET "Status" = 'running', "LeaseId" = {leaseId}, "LeaseUntil" = now() + {duration},
                "Attempts" = job."Attempts" + 1
            FROM candidate
            WHERE job."Id" = candidate."Id"
            RETURNING job."Id", job."Type", job."Payload"::text AS "Payload", job."LeaseId", job."Attempts"
            """).ToListAsync(ct);
        return claimed.SingleOrDefault();
    }

    public Task<int> FinishAsync(Lease lease, string? error, TimeSpan delay, CancellationToken ct)
    {
        if (error is null)
        {
            return data.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE jobs."Jobs"
                SET "Status" = 'succeeded', "FinishedAt" = now(), "LastError" = NULL,
                    "LeaseId" = NULL, "LeaseUntil" = NULL
                WHERE "Id" = {lease.Id} AND "LeaseId" = {lease.LeaseId} AND "LeaseUntil" > now()
                """, ct);
        }

        return data.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE jobs."Jobs"
            SET "Status" = CASE WHEN "Attempts" >= "MaxAttempts" THEN 'dead' ELSE 'pending' END,
                "FinishedAt" = CASE WHEN "Attempts" >= "MaxAttempts" THEN now() ELSE NULL END,
                "AvailableAt" = now() + {delay}, "LeaseId" = NULL, "LeaseUntil" = NULL, "LastError" = {error}
            WHERE "Id" = {lease.Id} AND "LeaseId" = {lease.LeaseId} AND "LeaseUntil" > now()
            """, ct);
    }

    public Task<List<JobSummary>> ListAsync(int offset, Guid? id, string? status, DateTimeOffset? before, CancellationToken ct) =>
        data.Jobs.Where(item => (id == null || item.Id == id) && (status == null || item.Status == status) &&
            (before == null || item.AvailableAt < before)).OrderByDescending(item => item.AvailableAt).ThenBy(item => item.Id)
            .Skip(Math.Max(0, offset)).Take(50)
            .Select(item => new JobSummary(item.Id, item.Type, item.Status, item.Attempts, item.MaxAttempts,
                item.AvailableAt, item.FinishedAt, item.LastError)).ToListAsync(ct);

    public async Task<bool> RetryAsync(Guid id, string actor, string reason, CancellationToken ct)
    {
        await using var transaction = await data.Database.BeginTransactionAsync(ct);
        var changed = await data.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE jobs."Jobs"
            SET "Status" = 'pending', "Attempts" = 0, "AvailableAt" = now(), "FinishedAt" = NULL, "LastError" = NULL
            WHERE "Id" = {id} AND "Status" = 'dead'
            """, ct);
        if (changed == 0)
            return false;
        await OperationsAudit.RecordAsync(data, actor, "job.redriven", id.ToString(), reason, ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
