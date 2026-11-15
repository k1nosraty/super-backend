# Transactional Outbox

The optional Outbox module records a job message in the same PostgreSQL transaction as its originating business write. It then transfers committed messages to the existing durable Jobs queue. Producer, relay, and consumer may run in different processes using the same PostgreSQL database.

## Enable and migrate

Enable `Modules__Jobs=true` and `Modules__Outbox=true`. Apply the migrations with the same flags before the API or worker starts:

```bash
Modules__Jobs=true Modules__Outbox=true ./scripts/db.sh apply
./scripts/db.sh check Outbox
```

For deployed images, supply the flags to the one-shot `--migrate` process and the relevant runtime processes. All producer transactions must use the database managed by the configured `ConnectionStrings__Main`. This implementation can span schemas in that database. It requires the business write and outbox insert to share the same physical connection and transaction. [EF Core transaction guidance](https://learn.microsoft.com/en-us/ef/core/saving/transactions)

| Setting | Default | Purpose |
| --- | --- | --- |
| `Modules__Outbox` | `false` | Registers the writer, inbox, relay, and outbox readiness check when Jobs is enabled |
| `Outbox__RunRelay` | `true` | Set false on a producer-only or consumer-only process |
| `Outbox__PollMilliseconds` | `1000` | Wait between polls when a batch is not full |
| `Outbox__BatchSize` | `100` | Maximum messages per relay transaction |
| `Jobs__RunWorker` | `true` | Set false on a producer-only or relay-only process |

## Write data and a message together

Reference the Outbox project and inject `ITransactionalOutbox`. Start an explicit transaction in the originating repository or application service. Pass that transaction to the writer and make a single commit:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Text.Json;
using SuperBackend.Jobs;
using SuperBackend.Outbox;

// operationId is a stable, nonempty ID for this submission.
await using var transaction = await db.Database.BeginTransactionAsync(ct);
db.Orders.Add(order);
await db.SaveChangesAsync(ct);
await outbox.RecordAsync(transaction.GetDbTransaction(),
    new JobSubmission(operationId, "orders.created",
        JsonSerializer.SerializeToElement(new { orderId = order.Id })), ct);
await transaction.CommitAsync(ct);
```

With ADO.NET, pass the active `NpgsqlTransaction` directly. A rollback removes both pending writes. Record failures must abort the originating business operation. A repeated message ID with equivalent JSON, the same type, and the same attempt limit is accepted. Reusing an ID with a different submission throws. Use a distinct ID for each distinct event or job, and reuse the original ID when retrying the same operation.

## Relay guarantees

Workers lock batches of committed outbox rows with `FOR UPDATE SKIP LOCKED`. Each batch inserts queue rows and records dispatch in a single PostgreSQL statement. If that statement fails, both actions roll back and the pending message can be picked up again. When a job with the same ID and submission already exists, the relay records dispatch without resetting its status or attempts.

A job with the same ID and different content is preserved. The outbox message receives `ConflictAt` and the relay logs a warning. This terminal state makes the collision visible without blocking other work. Inspect the two records and use a new unique ID if the original business message needs to be submitted again.

```sql
SELECT "Id", "Type", "CreatedAt", "ConflictAt"
FROM outbox."Messages"
WHERE "ConflictAt" IS NOT NULL
ORDER BY "ConflictAt" DESC;
```

Pending messages have both `DispatchedAt` and `ConflictAt` null. Monitor the age of the oldest pending message and the count of conflicts. The readiness probe verifies that both outbox tables and the Jobs table exist. A runtime producer needs permission to insert or deduplicate in `outbox."Messages"`. A relay needs read and update permissions there and insert, read, and conflict-update permission on `jobs."Jobs"`. A database-backed consumer using the inbox needs insert permission on `outbox."InboxReceipts"`. Give each process only its required PostgreSQL privileges.

## Handle repeated execution

Jobs delivery remains at least once. Use `IJobInbox` when a handler's effect can share a PostgreSQL transaction with its receipt. Keep the consumer name stable for the lifetime of the logical handler:

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
if (!await inbox.TryRecordAsync(transaction.GetDbTransaction(),
        "inventory.apply-order", job.Id, ct))
    return;

await ApplyOrderAsync(db, job.Payload, ct);
await db.SaveChangesAsync(ct);
await transaction.CommitAsync(ct);
```

An exception before commit rolls back both the receipt and the effect. If the process fails after commit and the queue retries the job, the receipt stops the effect from being applied again. Concurrent attempts use the same unique receipt key. Keep all database effects after the receipt check and in the supplied transaction. For external effects, propagate the job ID as an idempotency key to the external system and handle its delivery guarantees separately.

Retain outbox messages, jobs, and inbox receipts for the applicable retry and deduplication windows. The module exposes persistent state and leaves retention policy to the deployment. Integration tests inject a PostgreSQL relay failure after queue insertion and handler failures on both sides of commit.
