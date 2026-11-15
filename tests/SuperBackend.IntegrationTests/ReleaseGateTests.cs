using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SuperBackend.Identity;
using SuperBackend.Notes;
using Xunit;
using static SuperBackend.IntegrationTests.BackendTests;

namespace SuperBackend.IntegrationTests;

public sealed class ReleaseGateTests(BackendDatabase database) : IClassFixture<BackendDatabase>
{
    [Fact]
    public async Task ReadinessTracksEnabledMigrationsAndLivenessSurvivesUnreachableStorage()
    {
        var name = $"ready_{Guid.NewGuid():N}";
        await using var administration = new NpgsqlConnection(database.DatabaseConnection);
        await administration.OpenAsync();
        await using (var create = new NpgsqlCommand($"""CREATE DATABASE "{name}" """, administration))
            await create.ExecuteNonQueryAsync();

        var connection = new NpgsqlConnectionStringBuilder(database.DatabaseConnection) { Database = name };
        var options = new Dictionary<string, string?> { ["ConnectionStrings:Main"] = connection.ConnectionString };
        try
        {
            await using var app = database.CreateApp(options);
            using var client = CreateClient(app);
            await ExpectAsync(client.GetAsync("/health/ready"), HttpStatusCode.ServiceUnavailable);
            await app.Services.MigrateIdentityAsync();
            await ExpectAsync(client.GetAsync("/health/ready"), HttpStatusCode.ServiceUnavailable);
            await app.Services.MigrateNotesAsync();
            await ExpectAsync(client.GetAsync("/health/ready"), HttpStatusCode.OK);

            await using (var current = new NpgsqlConnection(connection.ConnectionString))
            {
                await current.OpenAsync();
                await using var future = new NpgsqlCommand("""
                    INSERT INTO identity."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('29990101000100_CompatibleAddition', '10.0.15')
                    """, current);
                await future.ExecuteNonQueryAsync();
            }

            await ExpectAsync(client.GetAsync("/health/ready"), HttpStatusCode.OK);
            connection.Port = 1;
            connection.Timeout = 1;
            options["ConnectionStrings:Main"] = connection.ConnectionString;
            await using var offline = database.CreateApp(options);
            using var probe = CreateClient(offline);
            await ExpectAsync(probe.GetAsync("/health/ready"), HttpStatusCode.ServiceUnavailable);
            await ExpectAsync(probe.GetAsync("/health/live"), HttpStatusCode.OK);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"""DROP DATABASE "{name}" WITH (FORCE)""", administration);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task RefreshRotatesOnceAcrossHostsAndLogoutLeavesTheOtherSessionActive()
    {
        await using var first = database.CreateApp();
        await using var second = database.CreateApp();
        using var a = CreateClient(first);
        using var b = CreateClient(second);
        var email = await CreateAccountAsync(a);
        var original = await LoginAsync(a, email, Password);
        var independent = await LoginAsync(b, email, Password);

        var competing = await Task.WhenAll(Refresh(a, original.RefreshToken), Refresh(b, original.RefreshToken));
        using var winner = Assert.Single(competing, response => response.StatusCode == HttpStatusCode.OK);
        using var loser = Assert.Single(competing, response => response.StatusCode == HttpStatusCode.Unauthorized);
        var rotated = (await winner.Content.ReadFromJsonAsync<Pair>())!;
        await ExpectAsync(Refresh(a, original.RefreshToken), HttpStatusCode.Unauthorized);

        Authorize(a, rotated.AccessToken);
        await ExpectAsync(a.PostAsync("/api/v1/auth/logout", null), HttpStatusCode.NoContent);
        await ExpectAsync(a.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized);
        await ExpectAsync(Refresh(b, rotated.RefreshToken), HttpStatusCode.Unauthorized);
        Authorize(b, independent.AccessToken);
        await ExpectAsync(b.GetAsync("/api/v1/me"), HttpStatusCode.OK);
        await ExpectAsync(Refresh(b, independent.RefreshToken), HttpStatusCode.OK);
    }

    [Fact]
    public async Task PrivilegedMfaAndOperatorRecoveryInvalidateExistingSessions()
    {
        await using var app = database.CreateApp(new() { ["Identity:RequirePrivilegedMfa"] = "true" });
        using var client = CreateClient(app);
        const string target = "recovery-reference@example.test";
        await app.Services.BootstrapAdministratorAsync(target, Password);
        var passwordOnly = await LoginAsync(client, target, Password);
        Authorize(client, passwordOnly.AccessToken);
        await ExpectAsync(client.GetAsync("/api/v1/admin/users"), HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Add("X-Current-Password", Password);
        var initial = await ReadAsync<JsonElement>(client.PostAsJsonAsync("/api/v1/auth/manage/2fa", new { }),
            HttpStatusCode.OK);
        var key = initial.GetProperty("sharedKey").GetString()!;
        Authorize(client, (await LoginAsync(client, target, Password)).AccessToken);
        await ExpectAsync(client.PostAsJsonAsync("/api/v1/auth/manage/2fa",
            new { enable = true, twoFactorCode = Totp.Now(key) }), HttpStatusCode.OK);
        client.DefaultRequestHeaders.Remove("X-Current-Password");

        var elevated = await LoginAsync(client, target, Password, Totp.Now(key));
        Authorize(client, elevated.AccessToken);
        await ExpectAsync(client.GetAsync("/api/v1/admin/users"), HttpStatusCode.OK);
        var refreshed = await ReadAsync<Pair>(Refresh(client, elevated.RefreshToken), HttpStatusCode.OK);
        Authorize(client, refreshed.AccessToken);
        await ExpectAsync(client.GetAsync("/api/v1/admin/users"), HttpStatusCode.OK);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            app.Services.RecoverAdministratorAsync(target, "staging-operator", "Only enrolled operator", "suspend", null));
        await app.Services.RecoverAdministratorAsync(target, "staging-operator", "Lost authenticator", "reset-mfa",
            "replacement-password-verified");
        await ExpectAsync(client.GetAsync("/api/v1/admin/users"), HttpStatusCode.Unauthorized);
        await ExpectAsync(Refresh(client, refreshed.RefreshToken), HttpStatusCode.Unauthorized);
        Authorize(client, (await LoginAsync(client, target, "replacement-password-verified")).AccessToken);
        await ExpectAsync(client.GetAsync("/api/v1/admin/users"), HttpStatusCode.Forbidden);

        await app.Services.RecoverAdministratorAsync(target, "staging-operator", "Offboarding", "suspend", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            app.Services.RecoverAdministratorAsync(AdminEmail, "staging-operator", "Last operator", "suspend", null));
    }

    [Fact]
    public async Task OpenApiAndProblemCodesDescribeTheRuntimeContract()
    {
        await using var app = database.CreateApp(new() { ["Modules:Jobs"] = "true", ["Jobs:RunWorker"] = "false" });
        using var client = CreateClient(app);
        var schema = await ReadAsync<JsonElement>(client.GetAsync("/openapi/v1.json"), HttpStatusCode.OK);
        var paths = schema.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/v1/notes").GetProperty("post").GetProperty("responses").TryGetProperty("201", out _));
        var retry = paths.GetProperty("/api/v1/admin/jobs/{id}/retry").GetProperty("post").GetProperty("responses");
        Assert.True(retry.TryGetProperty("204", out _));
        Assert.True(retry.TryGetProperty("409", out _));

        var problem = await ReadAsync<JsonElement>(client.PostAsync("/api/v1/auth/refresh",
            new StringContent("not-json")), HttpStatusCode.UnsupportedMediaType);
        Assert.Equal("http_415", problem.GetProperty("code").GetString());
    }

    private static Task<Pair> LoginAsync(HttpClient client, string email, string password, string? twoFactorCode = null) =>
        ReadAsync<Pair>(client.PostAsJsonAsync("/api/v1/auth/login", new { email, password, twoFactorCode }), HttpStatusCode.OK);

    private static Task<HttpResponseMessage> Refresh(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });

    private static void Authorize(HttpClient client, string accessToken) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    private sealed record Pair(string AccessToken, string RefreshToken);
}
