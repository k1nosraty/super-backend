using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SuperBackend.Jobs;
using SuperBackend.Outbox;
using Xunit;
using static SuperBackend.IntegrationTests.BackendTests;

namespace SuperBackend.IntegrationTests;

public sealed class NotesOutboxTests(BackendDatabase database) : IClassFixture<BackendDatabase>
{
    private const string Notes = "/api/v1/notes";

    [Fact]
    public async Task NotesCanCreateWithoutPublishingWhenOutboxIsEnabled()
    {
        await PrepareAsync();
        await using var app = database.CreateApp(Settings(publish: false));
        using var client = CreateClient(app);
        await AuthenticateAsync(client, AdminEmail);
        var created = await ReadAsync<CreatedNote>(client.PostAsJsonAsync(Notes,
            new { title = "Local note", body = "" }), HttpStatusCode.Created);

        Assert.Equal(0L, await ScalarAsync<long>(
            """SELECT count(*) FROM outbox."Messages" WHERE "Id" = @id""", created.Id));
    }

    [Fact]
    public async Task NoteAndMessageRollBackTogetherAndWorkerReplayDoesNotIncrementTwice()
    {
        await PrepareAsync();
        CreatedNote created;
        string ownerId;
        await using (var producer = database.CreateApp(Settings()))
        {
            using var client = CreateClient(producer);
            await AuthenticateAsync(client, AdminEmail);
            ownerId = (await ReadAsync<Owner>(client.GetAsync("/api/v1/me"), HttpStatusCode.OK)).Id;
            await ExecuteAsync("""
                CREATE SEQUENCE notes.test_outbox_failure;
                CREATE FUNCTION notes.test_reject_message() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Type" = 'notes.created' THEN
                        IF EXISTS (SELECT FROM notes."Notes" WHERE "Id" = NEW."Id") THEN
                            PERFORM nextval('notes.test_outbox_failure');
                        END IF;
                        RAISE EXCEPTION 'simulated outbox insert failure';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER test_reject_message BEFORE INSERT ON outbox."Messages"
                FOR EACH ROW EXECUTE FUNCTION notes.test_reject_message();
                """);
            try
            {
                await ExpectAsync(client.PostAsJsonAsync(Notes, new { title = "Rolled back", body = "" }),
                    HttpStatusCode.InternalServerError);
                Assert.True(await ScalarAsync<bool>("SELECT is_called FROM notes.test_outbox_failure"));
                Assert.Equal(0L, await ScalarAsync<long>("""
                    SELECT count(*) FROM notes."Notes" WHERE "Title" = 'Rolled back'
                    """));
                Assert.Equal(0L, await ScalarAsync<long>("""
                    SELECT count(*) FROM outbox."Messages" WHERE "Type" = 'notes.created'
                    """));
            }
            finally
            {
                await ExecuteAsync("""DROP TRIGGER test_reject_message ON outbox."Messages" """);
            }

            created = await ReadAsync<CreatedNote>(client.PostAsJsonAsync(Notes,
                new { title = "Private title", body = "Private body" }), HttpStatusCode.Created);
            await ExpectAsync(client.GetAsync($"{Notes}/{created.Id}"), HttpStatusCode.OK);
            Assert.Equal(1L, await ScalarAsync<long>("""
                SELECT count(*) FROM outbox."Messages"
                WHERE "Id" = @id AND "Type" = 'notes.created' AND "DispatchedAt" IS NULL
                  AND "Payload" = jsonb_build_object('ownerId', CAST(@owner AS text))
                """, created.Id, ownerId));
            Assert.Equal(0L, await ScalarAsync<long>(
                """SELECT count(*) FROM jobs."Jobs" WHERE "Id" = @id""", created.Id));
        }

        await ExecuteAsync("""
            CREATE SEQUENCE notes.test_ack_failure;
            CREATE FUNCTION notes.test_reject_ack() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."Type" = 'notes.created' AND NEW."Status" = 'succeeded' AND NEW."Attempts" = 1 THEN
                    PERFORM nextval('notes.test_ack_failure');
                    RAISE EXCEPTION 'simulated crash after handler commit';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER test_reject_ack BEFORE UPDATE ON jobs."Jobs"
            FOR EACH ROW EXECUTE FUNCTION notes.test_reject_ack();
            """);

        await using var consumer = database.CreateApp(Settings(publish: false, process: true));
        _ = consumer.Services;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (await ScalarAsync<long>("""
            SELECT count(*) FROM jobs."Jobs" WHERE "Id" = @id AND "Status" = 'succeeded' AND "Attempts" = 2
            """, created.Id) == 0)
        {
            await Task.Delay(100, timeout.Token);
        }

        Assert.True(await ScalarAsync<bool>("SELECT is_called FROM notes.test_ack_failure"));
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT count(*) FROM outbox."Messages" WHERE "Id" = @id AND "DispatchedAt" IS NOT NULL
            """, created.Id));
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT count(*) FROM outbox."InboxReceipts" WHERE "MessageId" = @id AND "Consumer" = 'notes.created'
            """, created.Id));
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT "TotalCreated" FROM notes."NoteCreationStats" WHERE "OwnerId" = @owner
            """, ownerId: ownerId));
    }

    [Fact]
    public async Task ConcurrentRequestsWithOneIdempotencyKeyPublishOneEvent()
    {
        await using var isolated = new BackendDatabase();
        await isolated.InitializeAsync();
        await PrepareAsync(isolated);
        await using var app = isolated.CreateApp(Settings());
        using var first = CreateClient(app);
        using var second = CreateClient(app);
        await AuthenticateAsync(first, AdminEmail);
        await AuthenticateAsync(second, AdminEmail);
        first.DefaultRequestHeaders.Add("Idempotency-Key", "concurrent-note");
        second.DefaultRequestHeaders.Add("Idempotency-Key", "concurrent-note");
        var payload = new { title = "Exactly one committed note", body = "" };
        var notes = await Task.WhenAll(
            ReadAsync<CreatedNote>(first.PostAsJsonAsync(Notes, payload), HttpStatusCode.Created),
            ReadAsync<CreatedNote>(second.PostAsJsonAsync(Notes, payload), HttpStatusCode.Created));
        Assert.Equal(notes[0].Id, notes[1].Id);
        await ExpectAsync(second.PostAsJsonAsync(Notes, new { title = "Changed payload", body = "" }), HttpStatusCode.Conflict);
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT count(*) FROM outbox."Messages" WHERE "Id" = @id AND "Type" = 'notes.created'
            """, notes[0].Id, connectionString: isolated.DatabaseConnection));
    }

    [Fact]
    public async Task OperatorsCanRedriveADifferentTypeCollisionOnlyOnceAndCannotOverrideInbox()
    {
        await PrepareAsync();
        await using var app = database.CreateApp(Settings());
        using var client = CreateClient(app);
        await AuthenticateAsync(client, AdminEmail);
        var eligible = Guid.NewGuid();
        var processed = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO outbox."Messages" ("Id", "Type", "Payload", "MaxAttempts", "CreatedAt", "ConflictAt")
            VALUES ('{eligible}', 'test.redrive', jsonb_build_object(), 5, now(), now()),
                   ('{processed}', 'test.redrive', jsonb_build_object(), 5, now(), now());
            INSERT INTO jobs."Jobs" ("Id", "Type", "Payload", "Status", "Attempts", "MaxAttempts", "AvailableAt")
            VALUES ('{eligible}', 'different.type', jsonb_build_object(), 'succeeded', 1, 5, now());
            INSERT INTO outbox."InboxReceipts" ("MessageId", "Consumer", "ProcessedAt")
            VALUES ('{processed}', 'test.redrive', now());
            """);
        const string root = "/api/v1/admin/outbox";
        var listing = await ReadAsync<Conflict[]>(client.GetAsync($"{root}?status=conflict&id={eligible}"), HttpStatusCode.OK);
        Assert.Equal(eligible, Assert.Single(listing).Id);
        await ExpectAsync(client.PostAsJsonAsync($"{root}/{processed}/redrive", new { reason = "Investigated collision" }),
            HttpStatusCode.Conflict);

        var accepted = await ReadAsync<Conflict>(client.PostAsJsonAsync($"{root}/{eligible}/redrive", new { reason = "Investigated collision" }),
            HttpStatusCode.Accepted);
        Assert.NotEqual(eligible, accepted.Id);
        await ExpectAsync(client.PostAsJsonAsync($"{root}/{eligible}/redrive", new { reason = "Repeated action" }), HttpStatusCode.Conflict);
        Assert.Equal(1L, await ScalarAsync<long>(
            """
            SELECT count(*) FROM identity."AccessAudits"
            WHERE "Action" = 'outbox.redriven' AND "UserId" LIKE @owner AND "Reason" = 'Investigated collision'
              AND "ActorId" IS NOT NULL AND "TraceId" IS NOT NULL
            """, ownerId: $"{eligible}/%"));
    }

    private sealed record Conflict(Guid Id);

    private async Task PrepareAsync(BackendDatabase? testDatabase = null)
    {
        await using var app = (testDatabase ?? database).CreateApp(Settings());
        await app.Services.MigrateJobsAsync();
        await app.Services.MigrateOutboxAsync();
    }

    private static Dictionary<string, string?> Settings(bool publish = true, bool process = false) => new()
    {
        ["Modules:Notes"] = "true",
        ["Modules:Jobs"] = "true",
        ["Modules:Outbox"] = "true",
        ["Notes:PublishCreatedJobs"] = publish.ToString(),
        ["Outbox:RunRelay"] = process.ToString(),
        ["Outbox:PollMilliseconds"] = "100",
        ["Jobs:RunWorker"] = process.ToString(),
        ["Jobs:PollMilliseconds"] = "100",
        ["Jobs:LeaseSeconds"] = "3",
        ["Jobs:AttemptTimeoutSeconds"] = "2"
    };

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.DatabaseConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, Guid? id = null, string? ownerId = null, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? database.DatabaseConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        if (id.HasValue)
            command.Parameters.AddWithValue("id", id.Value);
        if (ownerId is not null)
            command.Parameters.AddWithValue("owner", ownerId);
        return Assert.IsType<T>(await command.ExecuteScalarAsync());
    }

    private sealed record CreatedNote(Guid Id);
    private sealed record Owner(string Id);
}
