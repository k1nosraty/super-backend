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

Retain outbox messages, jobs, and inbox receipts for the applicable retry and deduplication windows. The maintenance command deletes eligible settled job, outbox, and inbox rows together. Configure its retention window to cover the deployment’s retry and deduplication requirements. Integration tests inject a PostgreSQL relay failure after queue insertion and handler failures on both sides of commit.

## Notes integration

To publish a job from `POST /api/v1/notes`, apply the migrations and use the same settings on the producer:

```bash
Modules__Notes=true Modules__Jobs=true Modules__Outbox=true Notes__PublishCreatedJobs=true ./scripts/db.sh apply
```

`Notes__PublishCreatedJobs` defaults to false. When enabled, the endpoint inserts the note and writes a `notes.created` outbox message in the same transaction. A write failure rolls both back. The note ID is the job ID. The payload contains the owner's ID and excludes the private note title and body.

The built-in handler updates `notes."NoteCreationStats"`. This projection counts successfully processed creation events per owner, including creations whose notes were later deleted. It starts counting with events from enabled producers. The handler inserts its `notes.created` inbox receipt and increments the counter in the same PostgreSQL transaction. A retried job therefore leaves the counter unchanged after its first successful commit.

Enable Notes, Jobs, and Outbox on every active Jobs worker that shares this queue. Workers can claim any job type. Keep at least one Outbox relay running. Turning publishing off allows the registered handler to finish the existing backlog.

The end-to-end test calls the authenticated Notes API, injects an outbox write failure, and verifies the note rolls back. It also injects a queue acknowledgement failure after the real handler commits and checks that the worker reclaims the job without incrementing the counter twice.

### Inspect and redrive

Use `GET /api/v1/admin/outbox?status=conflict&id=...` with `outbox.inspect`. Filter by ID, status, or UTC `before`. Follow the message ID in the Jobs API. For a confirmed different-type collision, send `POST /api/v1/admin/outbox/{id}/redrive` with a reason. The operation uses a new message ID and records the link and audit atomically. A repeat, a same-type collision, a dispatch without a conflict, or an existing inbox receipt returns 409. Investigate those states before performing any further operation. Redrive requires a privileged MFA session in production.
