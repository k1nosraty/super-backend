using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SuperBackend.Jobs;
using SuperBackend.Outbox;
using Xunit;
using static SuperBackend.IntegrationTests.BackendTests;

namespace SuperBackend.IntegrationTests;

public sealed class QueuedMailTests(BackendDatabase database) : IClassFixture<BackendDatabase>
{
    [Fact]
    public async Task InvitationSurvivesSmtpOutageAndAReplacementWorkerDeliversItsProtectedPayload()
    {
        var settings = Settings();
        var email = $"queued-{Guid.NewGuid():N}@example.test";
        await using (var failing = database.CreateApp(new(settings) { ["Smtp:Port"] = "1", ["Jobs:RunWorker"] = "true" }))
        {
            await failing.Services.MigrateJobsAsync();
            await failing.Services.MigrateOutboxAsync();
            using var client = CreateClient(failing);
            await AuthenticateAsync(client, AdminEmail);
            await ExpectAsync(client.PostAsJsonAsync("/api/v1/admin/invitations", new { email }), HttpStatusCode.NoContent);
            await WaitForAsync(async () => (await ReadJobsAsync(client)).Any(job => job.Type == "identity.mail.v1" &&
                job.Attempts >= 1 && job.LastError == "handler-failed"));

            var payload = await ReadMailPayloadAsync();
            Assert.DoesNotContain(email, payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Your account invitation", payload, StringComparison.Ordinal);

            await using var scope = failing.Services.CreateAsyncScope();
            var expired = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("identity-mail-envelope-v1").Protect(JsonSerializer.Serialize(new
                {
                    Recipient = email,
                    Subject = "Expired test code",
                    Body = "expired-fixture",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
                }));
            var expiredId = Guid.NewGuid();
            var queue = scope.ServiceProvider.GetRequiredService<IDurableJobQueue>();
            Assert.True(await queue.EnqueueAsync(expiredId, "identity.mail.v1", JsonSerializer.SerializeToElement(expired)));
            await WaitForAsync(async () => (await ReadJobsAsync(client)).Any(job => job.Id == expiredId &&
                job.Status == "succeeded" && job.Attempts == 1 && job.LastError is null));
        }

        settings["Jobs:RunWorker"] = "true";
        await using var replacement = database.CreateApp(settings);
        using var admin = CreateClient(replacement);
        await AuthenticateAsync(admin, AdminEmail);
        await WaitForAsync(async () => (await ReadJobsAsync(admin)).Any(job => job.Type == "identity.mail.v1" &&
            job.Status == "succeeded" && job.Attempts >= 2));
        var code = await ReadMailAsync(email, "Your account invitation");
        await ExpectAsync(admin.PostAsJsonAsync("/api/v1/auth/accept-invitation", new { email, code, password = Password }),
            HttpStatusCode.NoContent);
    }

    private static Dictionary<string, string?> Settings() => new()
    {
        ["Modules:Jobs"] = "true",
        ["Modules:Outbox"] = "true",
        ["Identity:QueueMail"] = "true",
        ["Outbox:PollMilliseconds"] = "100",
        ["Jobs:PollMilliseconds"] = "100",
        ["Jobs:RetrySeconds"] = "1"
    };

    private static Task<JobSummary[]> ReadJobsAsync(HttpClient client) =>
        ReadAsync<JobSummary[]>(client.GetAsync("/api/v1/admin/jobs"), HttpStatusCode.OK);

    private async Task<string> ReadMailPayloadAsync()
    {
        await using var connection = new NpgsqlConnection(database.DatabaseConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT "Payload"::text FROM jobs."Jobs" WHERE "Type" = 'identity.mail.v1'
            """, connection);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await condition())
            await Task.Delay(100, timeout.Token);
    }
}
