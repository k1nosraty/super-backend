using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SuperBackend.IntegrationTests;

internal sealed class TestOidcProvider : IAsyncDisposable
{
    public const string Client = "integration-client";
    public const string Secret = "integration-secret";
    public const string Subject = "corporate-user-sub";
    public const string Acr = "integration-mfa";
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Authorization> _codes = new();
    private readonly WebApplication _app;

    public string Issuer { get; private set; } = "";
    public string CurrentSubject { get; set; } = Subject;
    public string CurrentAcr { get; set; } = Acr;
    public string? TokenIssuer { get; set; }
    public string? TokenAudience { get; set; }
    public bool CodeVerified { get; private set; }

    public TestOidcProvider()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.MapGet("/.well-known/openid-configuration", () => new
        {
            issuer = Issuer,
            authorization_endpoint = Issuer + "/authorize",
            token_endpoint = Issuer + "/token",
            jwks_uri = Issuer + "/keys",
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            code_challenge_methods_supported = new[] { "S256" }
        });
        _app.MapGet("/keys", () =>
        {
            var key = _rsa.ExportParameters(false);
            return new
            {
                keys = new[] { new { kid = "integration", kty = "RSA", alg = "RS256", use = "sig",
                    n = Base64UrlEncoder.Encode(key.Modulus), e = Base64UrlEncoder.Encode(key.Exponent) } }
            };
        });
        _app.MapGet("/authorize", (HttpRequest request) =>
        {
            var query = request.Query;
            if (query["client_id"] != Client || query["response_type"] != "code" || query["code_challenge_method"] != "S256" ||
                query["redirect_uri"] != "http://localhost:5080/api/v1/oidc/callback" ||
                string.IsNullOrEmpty(query["nonce"]) || string.IsNullOrEmpty(query["state"]))
                return Results.BadRequest();

            var code = Guid.NewGuid().ToString("N");
            _codes[code] = new Authorization(query["code_challenge"].ToString(), query["redirect_uri"].ToString(),
                query["nonce"].ToString(), CurrentSubject, CurrentAcr);
            return Results.Redirect(QueryHelpers.AddQueryString(query["redirect_uri"].ToString(),
                new Dictionary<string, string?> { ["code"] = code, ["state"] = query["state"] }));
        });
        _app.MapPost("/token", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            if (form["client_id"] != Client || form["client_secret"] != Secret || form["grant_type"] != "authorization_code" ||
                !_codes.TryRemove(form["code"].ToString(), out var authorization) ||
                form["redirect_uri"] != authorization.Callback || authorization.Challenge != Base64UrlEncoder.Encode(
                    SHA256.HashData(Encoding.UTF8.GetBytes(form["code_verifier"].ToString()))))
                return Results.BadRequest(new { error = "invalid_grant" });

            CodeVerified = true;
            var jwt = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = TokenIssuer ?? Issuer,
                Audience = TokenAudience ?? Client,
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = "integration" },
                    SecurityAlgorithms.RsaSha256),
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = authorization.Subject,
                    ["nonce"] = authorization.Nonce,
                    ["acr"] = authorization.Acr,
                    ["email"] = "operator@example.test"
                }
            });
            return Results.Ok(new { access_token = "test-only-access-token", token_type = "Bearer", expires_in = 300, id_token = jwt });
        });
    }

    public async Task StartAsync()
    {
        await _app.StartAsync();
        Issuer = _app.Urls.Single();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _rsa.Dispose();
    }

    private sealed record Authorization(string Challenge, string Callback, string Nonce, string Subject, string Acr);
}
