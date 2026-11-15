using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SuperBackend.IntegrationTests;

public sealed class BackendTests(BackendDatabase database) : IClassFixture<BackendDatabase>
{
    internal const string AdminEmail = "operator@example.test";
    internal const string Password = "correct-horse-battery-staple";
    private const string Auth = "/api/v1/auth";
    private const string Me = "/api/v1/me";
    private const string Admin = "/api/v1/admin";
    private const string Notes = "/api/v1/notes";

    [Fact]
    public async Task VerifiedAccountCanRefreshAndResetPasswordAndRevokeAllSessions()
    {
        await using var app = database.CreateApp();
        using var client = CreateClient(app);
        var email = NewEmail();

        await ExpectAsync(client.GetAsync(Me), HttpStatusCode.Unauthorized);
        await ExpectAsync(client.PostAsJsonAsync(Auth + "/login", new { email = (string?)null, password = Password }),
            HttpStatusCode.BadRequest);
        await ExpectAsync(client.PostAsJsonAsync(Auth + "/forgotPassword", new { email = (string?)null }),
            HttpStatusCode.BadRequest);
        await ExpectAsync(client.PostAsJsonAsync(Auth + "/register", new { email, password = Password }), HttpStatusCode.OK);
        await ExpectAsync(client.PostAsJsonAsync(Auth + "/login", new { email, password = Password }), HttpStatusCode.Unauthorized);

        var confirmation = new Uri(await ReadMailAsync(email, "Confirm your email"));
        Assert.Equal("http://localhost:5080", confirmation.GetLeftPart(UriPartial.Authority));
        await ExpectAsync(client.GetAsync(confirmation.PathAndQuery), HttpStatusCode.OK);
        var tokens = await LoginAsync(client, email);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        await ExpectAsync(client.PutAsJsonAsync(Me, new { displayName = new string('x', 101) }), HttpStatusCode.BadRequest);
        var profile = await ReadAsync<Profile>(client.PutAsJsonAsync(Me, new { displayName = "  Ada  " }), HttpStatusCode.OK);
        Assert.Equal("Ada", profile.DisplayName);

        var refreshed = await ReadAsync<Tokens>(
            client.PostAsJsonAsync(Auth + "/refresh", new { tokens.RefreshToken }), HttpStatusCode.OK);
        await ExpectAsync(client.PostAsync(Auth + "/logout-all", null), HttpStatusCode.NoContent);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
        await ExpectAsync(client.GetAsync(Me), HttpStatusCode.Unauthorized);
        await ExpectAsync(client.PostAsJsonAsync(Auth + "/refresh", new { refreshed.RefreshToken }), HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Authorization = null;

        await ExpectAsync(client.PostAsJsonAsync(Auth + "/forgotPassword", new { email }), HttpStatusCode.OK);
        var resetCode = await ReadMailAsync(email, "Password reset code");
        const string newPassword = "a-new-password-for-this-user";
        await ExpectAsync(client.PostAsJsonAsync(Auth + "/resetPassword", new { email, resetCode, newPassword }), HttpStatusCode.OK);
        await ExpectAsync(client.PostAsJsonAsync(Auth + "/login", new { email, password = Password }), HttpStatusCode.Unauthorized);
        await LoginAsync(client, email, newPassword);
    }

    [Fact]
    public async Task ExistingTokensUseCurrentGrantsAndNotesStayPrivateToTheirOwner()
    {
        await using var app = database.CreateApp();
        using var admin = CreateClient(app);
        using var first = CreateClient(app);
        using var second = CreateClient(app);
        await AuthenticateAsync(admin, AdminEmail);
        await AuthenticateAsync(first, await CreateAccountAsync(first));
        await AuthenticateAsync(second, await CreateAccountAsync(second));

        var user1 = await ReadAsync<Profile>(first.GetAsync(Me), HttpStatusCode.OK);
        var user2 = await ReadAsync<Profile>(second.GetAsync(Me), HttpStatusCode.OK);
        await ExpectAsync(first.GetAsync(Admin + "/roles"), HttpStatusCode.Forbidden);
        await ExpectAsync(first.GetAsync(Notes), HttpStatusCode.Forbidden);

        var role = await ReadAsync<Role>(admin.PostAsJsonAsync(Admin + "/roles",
            new { name = $"Note editor {Guid.NewGuid():N}" }), HttpStatusCode.Created);
        await ExpectAsync(admin.PostAsJsonAsync(Admin + "/roles", new { name = role.Name }), HttpStatusCode.Conflict);
        var roles = await ReadAsync<Role[]>(admin.GetAsync(Admin + "/roles?limit=100"), HttpStatusCode.OK);
        var administrator = Assert.Single(roles, item => item.Name == "Administrator");
        await ExpectAsync(admin.PutAsync($"{Admin}/users/{user1.Id}/roles/{administrator.Id}", null), HttpStatusCode.Forbidden);

        var readGrant = $"{Admin}/roles/{role.Id}/permissions/notes.read";
        var writeGrant = $"{Admin}/roles/{role.Id}/permissions/notes.write";
        await ExpectAsync(admin.PutAsync($"{Admin}/roles/{role.Id}/permissions/identity.administration", null), HttpStatusCode.NotFound);
        await ExpectAsync(admin.PutAsync(readGrant, null), HttpStatusCode.NoContent);
        await ExpectAsync(admin.PutAsync(writeGrant, null), HttpStatusCode.NoContent);
        await ExpectAsync(admin.PutAsync(writeGrant, null), HttpStatusCode.NoContent);
        await ExpectAsync(admin.PutAsync($"{Admin}/users/{user1.Id}/roles/{role.Id}", null), HttpStatusCode.NoContent);
        await ExpectAsync(admin.PutAsync($"{Admin}/users/{user2.Id}/roles/{role.Id}", null), HttpStatusCode.NoContent);

        await ExpectAsync(first.PostAsJsonAsync(Notes, new { title = " ", body = "" }), HttpStatusCode.BadRequest);
        var note = await ReadAsync<Note>(first.PostAsJsonAsync(Notes, new { title = "Private", body = "" }), HttpStatusCode.Created);
        var noteUrl = $"{Notes}/{note.Id}";
        await ExpectAsync(first.GetAsync(noteUrl), HttpStatusCode.OK);
        await ExpectAsync(second.GetAsync(noteUrl), HttpStatusCode.NotFound);
        await ExpectAsync(second.PutAsJsonAsync(noteUrl, new { title = "Changed", body = "", note.Version }), HttpStatusCode.NotFound);
        await ExpectAsync(second.DeleteAsync(noteUrl), HttpStatusCode.NotFound);
        await ExpectAsync(first.PutAsJsonAsync(noteUrl, new { title = "Edited", body = "", note.Version }), HttpStatusCode.OK);
        await ExpectAsync(first.PutAsJsonAsync(noteUrl, new { title = "Stale", body = "", note.Version }), HttpStatusCode.Conflict);

        await ExpectAsync(admin.DeleteAsync(writeGrant), HttpStatusCode.NoContent);
        await ExpectAsync(first.DeleteAsync(noteUrl), HttpStatusCode.Forbidden);
        await ExpectAsync(first.GetAsync(noteUrl), HttpStatusCode.OK);
        await ExpectAsync(admin.DeleteAsync($"{Admin}/users/{user1.Id}/roles/{role.Id}"), HttpStatusCode.NoContent);
        await ExpectAsync(first.GetAsync(noteUrl), HttpStatusCode.Forbidden);

        var audit = await ReadAsync<AuditEntry[]>(admin.GetAsync(Admin + "/audit?limit=100"), HttpStatusCode.OK);
        Assert.Single(audit, entry => entry.RoleId == role.Id && entry.Action == "permission.granted" &&
            entry.Permission == "notes.write");
        Assert.Contains(audit, entry => entry.RoleId == role.Id && entry.Action == "role.removed" && entry.UserId == user1.Id);
    }

    [Fact]
    public async Task OptionalModulesAndRegistrationStayDisabledAndIdentityIsThrottled()
    {
        await using var app = database.CreateApp(new Dictionary<string, string?>
        {
            ["Modules:Notes"] = "false",
            ["Identity:AllowRegistration"] = "false",
            ["RateLimits:IdentityRequestsPerMinute"] = "2"
        });
        using var client = CreateClient(app);

        await ExpectAsync(client.GetAsync("/health/live"), HttpStatusCode.OK);
        await ExpectAsync(client.GetAsync("/health/ready"), HttpStatusCode.OK);
        await ExpectAsync(client.PostAsJsonAsync(Auth + "/register", new { email = NewEmail(), password = Password }),
            HttpStatusCode.NotFound);
        await AuthenticateAsync(client, AdminEmail);
        await ExpectAsync(client.GetAsync(Notes), HttpStatusCode.NotFound);
        var permissions = await ReadAsync<PermissionEntry[]>(client.GetAsync(Admin + "/permissions"), HttpStatusCode.OK);
        Assert.Empty(permissions);

        using var rejected = await client.PostAsJsonAsync(Auth + "/login", new { email = AdminEmail, password = Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
        using var schema = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, schema.StatusCode);
        var document = await schema.Content.ReadAsStringAsync();
        Assert.Contains("Bearer", document);
        Assert.DoesNotContain(Auth + "/register", document);
        Assert.DoesNotContain(Notes, document);
    }

    [Fact]
    public async Task RecoveryCodesAreSingleUseAndTwoFactorChangesRequireThePassword()
    {
        await using var app = database.CreateApp();
        using var client = CreateClient(app);
        var email = await CreateAccountAsync(client);
        await AuthenticateAsync(client, email);

        await ExpectAsync(client.PostAsJsonAsync(Auth + "/manage/2fa", new { }), HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Add("X-Current-Password", Password);
        var initial = await ReadAsync<TwoFactor>(client.PostAsJsonAsync(Auth + "/manage/2fa", new { }), HttpStatusCode.OK);
        // The initial creation of the authenticator key changes the security stamp.
        await ExpectAsync(client.GetAsync(Me), HttpStatusCode.Unauthorized);
        await AuthenticateAsync(client, email);
        var enabled = await ReadAsync<TwoFactor>(client.PostAsJsonAsync(Auth + "/manage/2fa",
            new { enable = true, twoFactorCode = Totp.Now(initial.SharedKey) }), HttpStatusCode.OK);
        Assert.NotEmpty(enabled.RecoveryCodes);
        client.DefaultRequestHeaders.Remove("X-Current-Password");
        client.DefaultRequestHeaders.Authorization = null;

        await ExpectAsync(client.PostAsJsonAsync(Auth + "/login", new { email, password = Password }), HttpStatusCode.Unauthorized);
        var credentials = new { email, password = Password, twoFactorRecoveryCode = enabled.RecoveryCodes[0] };
        await ExpectAsync(client.PostAsJsonAsync(Auth + "/login", credentials), HttpStatusCode.OK);
        await ExpectAsync(client.PostAsJsonAsync(Auth + "/login", credentials), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProductionRequiresHttpsAndOnlyAllowsConfiguredCorsOrigins()
    {
        var keys = Directory.CreateTempSubdirectory("super-backend-keys-");
        try
        {
            await using var app = database.CreateApp(new Dictionary<string, string?>
            {
                ["environment"] = "Production",
                ["Identity:PublicOrigin"] = "https://localhost",
                ["DataProtection:KeyDirectory"] = keys.FullName,
                ["Smtp:UseStartTls"] = "true",
                ["Smtp:From"] = "no-reply@example.test",
                ["Http:CorsOrigins:0"] = "https://app.example.test"
            });
            using var client = CreateClient(app);

            using var plain = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            plain.Headers.Add("X-Forwarded-Proto", "https");
            await ExpectAsync(client.SendAsync(plain), HttpStatusCode.BadRequest);
            await ExpectAsync(client.GetAsync("https://localhost/health/live"), HttpStatusCode.OK);

            using var allowed = new HttpRequestMessage(HttpMethod.Options, "https://localhost/api/v1/me");
            allowed.Headers.Add("Origin", "https://app.example.test");
            allowed.Headers.Add("Access-Control-Request-Method", "GET");
            using var preflight = await client.SendAsync(allowed);
            Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
            Assert.Equal("https://app.example.test", preflight.Headers.GetValues("Access-Control-Allow-Origin").Single());

            using var blocked = new HttpRequestMessage(HttpMethod.Options, "https://localhost/api/v1/me");
            blocked.Headers.Add("Origin", "https://unknown.example.test");
            blocked.Headers.Add("Access-Control-Request-Method", "GET");
            using var denied = await client.SendAsync(blocked);
            Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally
        {
            keys.Delete(recursive: true);
        }
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost:5080"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });

    private static async Task<string> CreateAccountAsync(HttpClient client)
    {
        var email = NewEmail();
        await ExpectAsync(client.PostAsJsonAsync(Auth + "/register", new { email, password = Password }), HttpStatusCode.OK);
        var link = new Uri(await ReadMailAsync(email, "Confirm your email"));
        await ExpectAsync(client.GetAsync(link.PathAndQuery), HttpStatusCode.OK);
        return email;
    }

    private static async Task AuthenticateAsync(HttpClient client, string email)
    {
        var tokens = await LoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
    }

    private static Task<Tokens> LoginAsync(HttpClient client, string email, string password = Password) =>
        ReadAsync<Tokens>(client.PostAsJsonAsync(Auth + "/login", new { email, password }), HttpStatusCode.OK);

    private static async Task<T> ReadAsync<T>(Task<HttpResponseMessage> request, HttpStatusCode expected)
    {
        using var response = await request;
        await CheckAsync(response, expected);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task ExpectAsync(Task<HttpResponseMessage> request, HttpStatusCode expected)
    {
        using var response = await request;
        await CheckAsync(response, expected);
    }

    private static async Task CheckAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(response.StatusCode == expected,
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}: " +
            $"expected {(int)expected}, got {(int)response.StatusCode}. " +
            (response.IsSuccessStatusCode ? "" : await response.Content.ReadAsStringAsync()));
    }

    private static async Task<string> ReadMailAsync(string recipient, string subject)
    {
        using var inbox = new HttpClient
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("SUPERBACKEND_TEST_MAILPIT") ?? "http://localhost:8025")
        };
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var result = await inbox.GetFromJsonAsync<MailSearch>("/api/v1/search?query=" + Uri.EscapeDataString($"to:{recipient}"));
            var match = result?.Messages.FirstOrDefault(message => message.Subject == subject);
            if (match is not null)
            {
                var email = await inbox.GetFromJsonAsync<MailBody>($"/api/v1/message/{match.Id}");
                return email!.Text.Trim();
            }
            await Task.Delay(TimeSpan.FromMilliseconds(150));
        }
        throw new InvalidOperationException($"Mailpit did not receive an email with subject '{subject}'.");
    }

    private static string NewEmail() => $"test-{Guid.NewGuid():N}@example.test";

    private sealed record Profile(string Id, string? DisplayName);
    private sealed record Role(string Id, string Name);
    private sealed record Note(Guid Id, Guid Version);
    private sealed record Tokens(string AccessToken, string RefreshToken);
    private sealed record PermissionEntry(string Name);
    private sealed record AuditEntry(string RoleId, string Action, string? UserId, string? Permission);
    private sealed record MailSearch(MailHeader[] Messages);
    private sealed record MailHeader(string Id, string Subject);
    private sealed record MailBody(string Text);
    private sealed record TwoFactor(string SharedKey, string[] RecoveryCodes);
}
