using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;
using static SuperBackend.IntegrationTests.BackendTests;

namespace SuperBackend.IntegrationTests;

public sealed class FederationTests(BackendDatabase database) : IClassFixture<BackendDatabase>
{
    [Fact]
    public async Task OrganizationCodeFlowUsesPkceAndOnlySignsInExplicitlyLinkedEligibleAccounts()
    {
        await using var provider = new TestOidcProvider();
        await provider.StartAsync();
        await using var app = database.CreateApp(new Dictionary<string, string?>
        {
            ["Modules:Federation"] = "true",
            ["Oidc:Authority"] = provider.Issuer,
            ["Oidc:ClientId"] = TestOidcProvider.Client,
            ["Oidc:ClientSecret"] = TestOidcProvider.Secret,
            ["Oidc:RequiredAcr"] = TestOidcProvider.Acr
        });
        using var admin = CreateClient(app);
        using var member = CreateClient(app);
        using var browser = CreateClient(app);
        await AuthenticateAsync(admin, AdminEmail);
        await AuthenticateAsync(member, await CreateAccountAsync(member));
        var local = await ReadAsync<UserView>(member.GetAsync("/api/v1/me"), HttpStatusCode.OK);
        var administrator = await ReadAsync<UserView>(admin.GetAsync("/api/v1/me"), HttpStatusCode.OK);

        using var unlinked = await CompleteLoginAsync(browser);
        Assert.Equal(HttpStatusCode.Unauthorized, unlinked.StatusCode);
        Assert.True(provider.CodeVerified);

        const string links = "/api/v1/admin/oidc-links";
        var link = new { userId = local.Id, subject = TestOidcProvider.Subject };
        await ExpectAsync(member.PutAsJsonAsync(links, link), HttpStatusCode.Forbidden);
        await ExpectAsync(admin.PutAsJsonAsync(links, new { userId = administrator.Id, link.subject }), HttpStatusCode.Forbidden);
        await ExpectAsync(admin.PutAsJsonAsync(links, link), HttpStatusCode.NoContent);

        using var invalidState = await CompleteLoginAsync(browser, corruptState: true);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidState.StatusCode);
        provider.TokenIssuer = "https://unrelated.example.test";
        using var invalidIssuer = await CompleteLoginAsync(browser);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidIssuer.StatusCode);
        provider.TokenIssuer = null;

        provider.TokenAudience = "other-application";
        using var invalidAudience = await CompleteLoginAsync(browser);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidAudience.StatusCode);
        provider.TokenAudience = null;

        var statusRoute = $"/api/v1/admin/users/{local.Id}/status";
        await ExpectAsync(admin.PutAsJsonAsync(statusRoute, new { disabled = true }), HttpStatusCode.NoContent);
        using var disabled = await CompleteLoginAsync(browser);
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
        await ExpectAsync(admin.PutAsJsonAsync(statusRoute, new { disabled = false }), HttpStatusCode.NoContent);

        provider.CurrentAcr = "password-only";
        using var weak = await CompleteLoginAsync(browser);
        Assert.Equal(HttpStatusCode.Unauthorized, weak.StatusCode);
        provider.CurrentAcr = TestOidcProvider.Acr;

        using var callback = await CompleteLoginAsync(browser);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/api/v1/oidc/session", callback.Headers.Location!.OriginalString);
        var cookie = Cookies(callback);
        Assert.Contains("SuperBackend.OidcExchange=", cookie, StringComparison.Ordinal);
        await ExpectAsync(browser.SendAsync(Exchange(cookie, "https://untrusted.example.test")), HttpStatusCode.Forbidden);
        var exchanged = await Task.WhenAll(
            browser.SendAsync(Exchange(cookie, "http://localhost:5080")),
            browser.SendAsync(Exchange(cookie, "http://localhost:5080")));
        using var accepted = Assert.Single(exchanged, response => response.StatusCode == HttpStatusCode.OK);
        using var rejected = Assert.Single(exchanged, response => response.StatusCode == HttpStatusCode.Unauthorized);
        var tokens = (await accepted.Content.ReadFromJsonAsync<Bearer>())!;
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var signedIn = await ReadAsync<UserView>(browser.GetAsync("/api/v1/me"), HttpStatusCode.OK);
        Assert.Equal(local.Id, signedIn.Id);
        await ExpectAsync(browser.SendAsync(Exchange(cookie, "http://localhost:5080")), HttpStatusCode.Unauthorized);
        await ExpectAsync(browser.GetAsync("/api/v1/admin/users"), HttpStatusCode.Forbidden);
        await ExpectAsync(admin.PostAsJsonAsync(links + "/remove", link), HttpStatusCode.NoContent);
        await ExpectAsync(browser.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized);
        await ExpectAsync(browser.SendAsync(Exchange(cookie, "http://localhost:5080")), HttpStatusCode.Unauthorized);
    }

    private static async Task<HttpResponseMessage> CompleteLoginAsync(HttpClient browser, bool corruptState = false)
    {
        using var challenge = await browser.GetAsync("/api/v1/oidc/login");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        using var provider = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var authorization = await provider.GetAsync(challenge.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, authorization.StatusCode);
        var path = authorization.Headers.Location!.PathAndQuery;
        if (corruptState)
            path = path[..path.IndexOf('?')] + "?code=fixture&state=invalid";
        using var callback = new HttpRequestMessage(HttpMethod.Get, path);
        callback.Headers.TryAddWithoutValidation("Cookie", Cookies(challenge));
        return await browser.SendAsync(callback);
    }

    private static string Cookies(HttpResponseMessage response) =>
        string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0]));

    private static HttpRequestMessage Exchange(string cookie, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/oidc/exchange");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        return request;
    }

    private sealed record UserView(string Id);
    private sealed record Bearer(string AccessToken);
}
