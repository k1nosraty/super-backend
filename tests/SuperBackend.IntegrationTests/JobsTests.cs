using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SuperBackend.Jobs;
using Xunit;
using static SuperBackend.IntegrationTests.BackendTests;

namespace SuperBackend.IntegrationTests;

public sealed class JobsTests(BackendDatabase database) : IClassFixture<BackendDatabase>
{
    private const string Jobs = "/api/v1/admin/jobs";

    [Fact]
    public async Task OptionalModulesDoNotExposeRoutesWhenDisabled()
    {
        await using var app = database.CreateApp();
        using var client = CreateClient(app);
        await AuthenticateAsync(client, AdminEmail);
        await ExpectAsync(client.GetAsync(Jobs), HttpStatusCode.NotFound);
        await ExpectAsync(client.GetAsync("/api/v1/tenants"), HttpStatusCode.NotFound);
        await ExpectAsync(client.GetAsync("/api/v1/oidc/login"), HttpStatusCode.NotFound);
        Assert.Null(app.Services.GetService<IDurableJobQueue>());
    }

    [Fact]
    public async Task QueuePersistsAcrossHostsAndCompetingWorkersRetryRecoverAndDeadLetter()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Modules:Jobs"] = "true",
            ["Jobs:RunWorker"] = "false",
            ["Jobs:PollMilliseconds"] = "75",
            ["Jobs:LeaseSeconds"] = "4",
            ["Jobs:AttemptTimeoutSeconds"] = "2",
            ["Jobs:RetrySeconds"] = "1"
        };
        var success = Guid.NewGuid();
        var flaky = Guid.NewGuid();
        var dead = Guid.NewGuid();
        var abandoned = Guid.NewGuid();
        var exhausted = Guid.NewGuid();
        var deadline = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(new { value = 4 });

        await using (var producer = database.CreateApp(settings))
        {
            await producer.Services.MigrateJobsAsync();
            await using var scope = producer.Services.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<IDurableJobQueue>();
            Assert.True(await queue.EnqueueAsync(success, "pass", payload));
            Assert.False(await queue.EnqueueAsync(success, "fail", payload));
            Assert.True(await queue.EnqueueAsync(flaky, "flaky", payload));
            Assert.True(await queue.EnqueueAsync(dead, "fail", payload, 1));
            Assert.True(await queue.EnqueueAsync(abandoned, "pass", payload));
            Assert.True(await queue.EnqueueAsync(exhausted, "pass", payload, 1));
            Assert.True(await queue.EnqueueAsync(deadline, "slow", payload, 1));
            await ExpireAsync(abandoned, exhausted);
        }

        var calls = new ConcurrentDictionary<Guid, int>();
        settings["Jobs:RunWorker"] = "true";
        await using var first = Worker();
        await using var second = Worker();
        using var writer = CreateClient(first);
        using var reader = CreateClient(second);
        await ExpectAsync(reader.GetAsync(Jobs), HttpStatusCode.Unauthorized);
        await AuthenticateAsync(writer, AdminEmail);

        var settled = await WaitForAsync(writer, rows => rows.Count(row => row.Status is "succeeded" or "dead") == 6);
        AssertJob(settled, success, "succeeded", 1);
        AssertJob(settled, flaky, "succeeded", 2);
        AssertJob(settled, dead, "dead", 1, "handler-failed");
        AssertJob(settled, abandoned, "succeeded", 2);
        AssertJob(settled, exhausted, "dead", 1, "lease-expired");
        AssertJob(settled, deadline, "dead", 1, "deadline-exceeded");
        Assert.Equal(1, calls[success]);
        Assert.Equal(2, calls[flaky]);
        Assert.Equal(1, calls[abandoned]);
        Assert.False(calls.ContainsKey(exhausted));

        await ExpectAsync(writer.PostAsync($"{Jobs}/{success}/retry", null), HttpStatusCode.Conflict);
        await ExpectAsync(writer.PostAsync($"{Jobs}/{dead}/retry", null), HttpStatusCode.NoContent);
        await WaitForAsync(writer, rows => calls[dead] == 2 && rows.Any(row => row.Id == dead && row.Status == "dead"));

        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Worker() =>
            database.CreateApp(settings).WithWebHostBuilder(host => host.ConfigureTestServices(services =>
            {
                foreach (var type in new[] { "pass", "flaky", "fail", "slow" })
                    services.AddSingleton<IDurableJobHandler>(new TestHandler(type, calls));
            }));
    }

    private async Task ExpireAsync(Guid abandoned, Guid exhausted)
    {
        await using var connection = new NpgsqlConnection(database.DatabaseConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE jobs."Jobs"
            SET "Status" = 'running', "Attempts" = 1, "LeaseId" = @lease, "LeaseUntil" = now() - interval '1 second'
            WHERE "Id" IN (@abandoned, @exhausted)
            """, connection);
        command.Parameters.AddWithValue("lease", Guid.NewGuid());
        command.Parameters.AddWithValue("abandoned", abandoned);
        command.Parameters.AddWithValue("exhausted", exhausted);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<JobSummary[]> WaitForAsync(HttpClient client, Func<JobSummary[], bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        JobSummary[] latest = [];
        while (!deadline.IsCancellationRequested)
        {
            latest = await ReadAsync<JobSummary[]>(client.GetAsync(Jobs), HttpStatusCode.OK);
            if (condition(latest))
                return latest;
            await Task.Delay(120);
        }
        throw new InvalidOperationException("Jobs did not settle: " +
            string.Join(", ", latest.Select(job => $"{job.Type}:{job.Status}:{job.Attempts}")));
    }

    private static void AssertJob(IEnumerable<JobSummary> jobs, Guid id, string status, int attempts, string? error = null)
    {
        var job = Assert.Single(jobs, item => item.Id == id);
        Assert.Equal(status, job.Status);
        Assert.Equal(attempts, job.Attempts);
        Assert.Equal(error, job.LastError);
    }

    private sealed class TestHandler(string type, ConcurrentDictionary<Guid, int> calls) : IDurableJobHandler
    {
        public string Type => type;

        public async Task HandleAsync(JobExecution job, CancellationToken ct)
        {
            calls.AddOrUpdate(job.Id, 1, (_, count) => count + 1);
            Assert.Equal(4, job.Payload.GetProperty("value").GetInt32());
            if (type == "slow")
                await Task.Delay(Timeout.Infinite, ct);
            if (type == "fail" || (type == "flaky" && job.Attempt == 1))
                throw new InvalidOperationException("Controlled failure.");
        }
    }
}
