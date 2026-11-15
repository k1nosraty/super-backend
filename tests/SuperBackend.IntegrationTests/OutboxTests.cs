using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SuperBackend.Jobs;
using SuperBackend.Outbox;
using Xunit;

namespace SuperBackend.IntegrationTests;

public sealed class OutboxTests(BackendDatabase database) : IClassFixture<BackendDatabase>
{
    [Fact]
    public async Task OutboxIsOptionalAndRequiresTheJobsModule()
    {
        await using var disabled = database.CreateApp(new() { ["Modules:Jobs"] = "true", ["Jobs:RunWorker"] = "false" });
        Assert.Null(disabled.Services.GetService<ITransactionalOutbox>());
        Assert.Null(disabled.Services.GetService<IJobInbox>());

        await using var invalid = database.CreateApp(new() { ["Modules:Outbox"] = "true" });
        var error = Assert.Throws<InvalidOperationException>(() => _ = invalid.Services);
        Assert.Contains("Modules:Outbox requires Modules:Jobs", error.Message);
    }

    [Fact]
    public async Task BusinessWriteAndMessageCommitOrRollBackTogether()
    {
        await PrepareAsync();
        await using var app = database.CreateApp(Settings());
        var outbox = app.Services.GetRequiredService<ITransactionalOutbox>();
        var rolledBack = Guid.NewGuid();
        var committed = Guid.NewGuid();

        await using var business = new BusinessData(new DbContextOptionsBuilder<BusinessData>()
            .UseNpgsql(database.DatabaseConnection).Options);
        await using (var transaction = await business.Database.BeginTransactionAsync())
        {
            business.Orders.Add(new Order(rolledBack));
            await business.SaveChangesAsync();
            await outbox.RecordAsync(transaction.GetDbTransaction(), Message(rolledBack, "orders.created"));
            Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM test_business."Orders" WHERE "Id" = @id""", rolledBack));
            Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM outbox."Messages" WHERE "Id" = @id""", rolledBack));
            await transaction.RollbackAsync();
        }

        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM test_business."Orders" WHERE "Id" = @id""", rolledBack));
        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM outbox."Messages" WHERE "Id" = @id""", rolledBack));

        await using (var transaction = await business.Database.BeginTransactionAsync())
        {
            business.Orders.Add(new Order(committed));
            await business.SaveChangesAsync();
            await outbox.RecordAsync(transaction.GetDbTransaction(), Message(committed, "orders.created"));
            await outbox.RecordAsync(transaction.GetDbTransaction(), Message(committed, "orders.created"));
            await transaction.CommitAsync();
        }

        var conflictingWrite = Guid.NewGuid();
        await using (var transaction = await business.Database.BeginTransactionAsync())
        {
            business.Orders.Add(new Order(conflictingWrite));
            await business.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                outbox.RecordAsync(transaction.GetDbTransaction(), Message(committed, "orders.edited")));
            await transaction.RollbackAsync();
        }

        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM test_business."Orders" WHERE "Id" = @id""", conflictingWrite));
        Assert.Equal(1L, await CountAsync("""SELECT count(*) FROM test_business."Orders" WHERE "Id" = @id""", committed));
        Assert.Equal(1L, await CountAsync("""
            SELECT count(*) FROM outbox."Messages"
            WHERE "Id" = @id AND "Type" = 'orders.created' AND "DispatchedAt" IS NULL
            """, committed));
        Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM jobs."Jobs" WHERE "Id" = @id""", committed));
    }

    [Fact]
    public async Task RelayFailureRollsBackTheQueueInsertAndRestartDeduplicatesDispatch()
    {
        await PrepareAsync();
        var fresh = Guid.NewGuid();
        var duplicate = Guid.NewGuid();
        var conflict = Guid.NewGuid();

        await using (var producer = database.CreateApp(Settings()))
        {
            using var ready = BackendTests.CreateClient(producer);
            await BackendTests.ExpectAsync(ready.GetAsync("/health/ready"), HttpStatusCode.OK);
            await using var scope = producer.Services.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<IDurableJobQueue>();
            Assert.True(await queue.EnqueueAsync(duplicate, "existing", Payload(), 3));
            Assert.True(await queue.EnqueueAsync(conflict, "existing", Payload(), 3));
            await ExecuteAsync("""
                UPDATE jobs."Jobs" SET "Status" = 'succeeded', "Attempts" = 1, "FinishedAt" = now()
                WHERE "Id" = @id
                """, duplicate);
            await RecordBatchAsync(producer.Services, Message(fresh, "relay-failure"),
                Message(duplicate, "existing"), Message(conflict, "different"));
        }

        await ExecuteAsync("""
            CREATE SEQUENCE test_business.relay_attempts;
            CREATE FUNCTION test_business.fail_dispatch() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."Type" = 'relay-failure' AND NEW."DispatchedAt" IS NOT NULL THEN
                    PERFORM nextval('test_business.relay_attempts');
                    RAISE EXCEPTION 'simulated failure after queue insert';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER reject_dispatch BEFORE UPDATE ON outbox."Messages"
            FOR EACH ROW EXECUTE FUNCTION test_business.fail_dispatch();
            """);
        try
        {
            await using var failingRelay = database.CreateApp(Settings(relay: true));
            _ = failingRelay.Services;
            await WaitForAsync(() => ScalarAsync<bool>("SELECT is_called FROM test_business.relay_attempts"));
            Assert.Equal(0L, await CountAsync("""SELECT count(*) FROM jobs."Jobs" WHERE "Id" = @id""", fresh));
            Assert.Equal(1L, await CountAsync("""
                SELECT count(*) FROM outbox."Messages"
                WHERE "Id" = @id AND "DispatchedAt" IS NULL AND "ConflictAt" IS NULL
                """, fresh));
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER reject_dispatch ON outbox.\"Messages\"");
        }

        await using var first = database.CreateApp(Settings(relay: true));
        await using var second = database.CreateApp(Settings(relay: true));
        _ = first.Services;
        _ = second.Services;
        await WaitForAsync(async () => await CountAsync("""
            SELECT count(*) FROM outbox."Messages" WHERE "Id" = @id AND "ConflictAt" IS NOT NULL
            """, conflict) == 1);
        Assert.Equal(1L, await CountAsync("""
            SELECT count(*) FROM outbox."Messages" WHERE "Id" = @id AND "DispatchedAt" IS NOT NULL
            """, fresh));
        Assert.Equal(1L, await CountAsync("""
            SELECT count(*) FROM jobs."Jobs" WHERE "Id" = @id AND "Status" = 'pending'
            """, fresh));
        Assert.Equal(1L, await CountAsync("""
            SELECT count(*) FROM jobs."Jobs" WHERE "Id" = @id AND "Status" = 'succeeded' AND "Attempts" = 1
            """, duplicate));
        Assert.Equal("existing", await ScalarAsync<string>("""SELECT "Type" FROM jobs."Jobs" WHERE "Id" = @id""", conflict));
    }

    [Fact]
    public async Task WorkerRetriesBothFailureWindowsButCommitsEachBusinessEffectOnce()
    {
        await PrepareAsync();
        var before = Guid.NewGuid();
        var after = Guid.NewGuid();
        await using (var producer = database.CreateApp(Settings()))
        {
            await RecordBatchAsync(producer.Services, Message(before, "outbox-effect", "before-commit"),
                Message(after, "outbox-effect", "after-commit"));
        }

        await using var host = database.CreateApp(Settings(relay: true, worker: true)).WithWebHostBuilder(web =>
            web.ConfigureTestServices(services => services.AddScoped<IDurableJobHandler>(provider =>
                new EffectHandler(database.DatabaseConnection, provider.GetRequiredService<IJobInbox>()))));
        _ = host.Services;

        foreach (var id in new[] { before, after })
        {
            await WaitForAsync(async () => await CountAsync("""
                SELECT count(*) FROM jobs."Jobs" WHERE "Id" = @id AND "Status" = 'succeeded' AND "Attempts" = 2
                """, id) == 1);
            Assert.Equal(1, await ScalarAsync<int>("""SELECT "Count" FROM test_business."Effects" WHERE "Id" = @id""", id));
            Assert.Equal(1L, await CountAsync("""
                SELECT count(*) FROM outbox."InboxReceipts" WHERE "MessageId" = @id AND "Consumer" = 'integration.effect'
                """, id));
        }
    }

    private async Task PrepareAsync()
    {
        await using var migration = database.CreateApp(Settings());
        await migration.Services.MigrateJobsAsync();
        await migration.Services.MigrateOutboxAsync();
        await ExecuteAsync("""
            CREATE SCHEMA IF NOT EXISTS test_business;
            CREATE TABLE IF NOT EXISTS test_business."Orders" ("Id" uuid PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS test_business."Effects" ("Id" uuid PRIMARY KEY, "Count" int NOT NULL);
            TRUNCATE outbox."Messages", outbox."InboxReceipts", jobs."Jobs";
            """);
    }

    private static Dictionary<string, string?> Settings(bool relay = false, bool worker = false) => new()
    {
        ["Modules:Jobs"] = "true",
        ["Modules:Outbox"] = "true",
        ["Outbox:RunRelay"] = relay.ToString(),
        ["Outbox:PollMilliseconds"] = "100",
        ["Jobs:RunWorker"] = worker.ToString(),
        ["Jobs:PollMilliseconds"] = "100",
        ["Jobs:RetrySeconds"] = "1"
    };

    private static JsonElement Payload(string mode = "normal") => JsonSerializer.SerializeToElement(new { mode });
    private static JobSubmission Message(Guid id, string type, string mode = "normal") => new(id, type, Payload(mode), 3);

    private async Task RecordBatchAsync(IServiceProvider services, params JobSubmission[] messages)
    {
        var outbox = services.GetRequiredService<ITransactionalOutbox>();
        await using var connection = new NpgsqlConnection(database.DatabaseConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var message in messages)
            await outbox.RecordAsync(transaction, message);
        await transaction.CommitAsync();
    }

    private async Task ExecuteAsync(string sql, Guid? id = null)
    {
        await using var connection = new NpgsqlConnection(database.DatabaseConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        if (id is Guid value)
            command.Parameters.AddWithValue("id", value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, Guid? id = null)
    {
        await using var connection = new NpgsqlConnection(database.DatabaseConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        if (id is Guid value)
            command.Parameters.AddWithValue("id", value);
        return Assert.IsType<T>(await command.ExecuteScalarAsync());
    }

    private Task<long> CountAsync(string sql, Guid id) => ScalarAsync<long>(sql, id);

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!timeout.IsCancellationRequested)
        {
            if (await condition())
                return;
            await Task.Delay(100);
        }
        Assert.Fail("The expected outbox or job state was not reached.");
    }

    private sealed record Order(Guid Id);

    private sealed class BusinessData(DbContextOptions<BusinessData> options) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Order>().ToTable("Orders", "test_business");
    }

    private sealed class EffectHandler(string connectionString, IJobInbox inbox) : IDurableJobHandler
    {
        public string Type => "outbox-effect";

        public async Task HandleAsync(JobExecution job, CancellationToken ct)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            if (!await inbox.TryRecordAsync(transaction, "integration.effect", job.Id, ct))
                return;

            await using var effect = new NpgsqlCommand("""
                INSERT INTO test_business."Effects" AS effect ("Id", "Count") VALUES (@id, 1)
                ON CONFLICT ("Id") DO UPDATE SET "Count" = effect."Count" + 1
                """, connection, transaction);
            effect.Parameters.AddWithValue("id", job.Id);
            await effect.ExecuteNonQueryAsync(ct);
            if (job.Attempt == 1 && job.Payload.GetProperty("mode").GetString() == "before-commit")
                throw new InvalidOperationException("Simulated crash before committing the effect.");
            await transaction.CommitAsync(ct);
            if (job.Attempt == 1)
                throw new InvalidOperationException("Simulated crash after committing the effect.");
        }
    }
}
