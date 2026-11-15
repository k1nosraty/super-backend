using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SuperBackend.Identity;

namespace SuperBackend.Federation;

public static class FederationModule
{
    internal const string Remote = "OrganizationOidc";
    internal const string Exchange = "OrganizationOidc.Exchange";
    internal const string Callback = "/api/v1/oidc/callback";

    public static void AddFederationModule(this WebApplicationBuilder builder, IMvcBuilder mvc)
    {
        mvc.AddApplicationPart(typeof(FederationModule).Assembly);
        var section = builder.Configuration.GetSection("Oidc");
        var allowHttp = builder.Environment.IsDevelopment();
        if (!Uri.TryCreate(section["Authority"], UriKind.Absolute, out var authority) ||
            !IsSafeUri(authority, allowHttp))
            throw new InvalidOperationException("Oidc:Authority must use HTTPS or a development loopback HTTP address.");

        var issuer = string.IsNullOrWhiteSpace(section["Issuer"]) ? authority.AbsoluteUri.TrimEnd('/') : section["Issuer"]!;
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || !IsSafeUri(issuerUri, allowHttp))
            throw new InvalidOperationException("Oidc:Issuer must be a trusted issuer URL.");

        var settings = new FederationSettings(authority, issuer, Required(section["ClientId"], "Oidc:ClientId"),
            Required(section["ClientSecret"], "Oidc:ClientSecret"),
            Required(builder.Configuration["Identity:PublicOrigin"], "Identity:PublicOrigin"), section["RequiredAcr"]);
        builder.Services.AddSingleton(settings);

        builder.Services.AddAuthentication()
            .AddCookie(Exchange, options =>
            {
                options.Cookie.Name = "SuperBackend.OidcExchange";
                options.Cookie.Path = "/api/v1/oidc";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(2);
                options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(Remote, options => Configure(options, settings, allowHttp));
    }

    private static void Configure(OpenIdConnectOptions options, FederationSettings settings, bool allowHttp)
    {
        options.Authority = settings.Authority.AbsoluteUri;
        options.RequireHttpsMetadata = !allowHttp || settings.Authority.Scheme == Uri.UriSchemeHttps;
        options.ClientId = settings.ClientId;
        options.ClientSecret = settings.ClientSecret;
        options.SignInScheme = Exchange;
        options.CallbackPath = Callback;
        options.ResponseType = "code";
        options.UsePkce = true;
        options.SaveTokens = false;
        options.MapInboundClaims = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.TokenValidationParameters.ValidIssuer = settings.Issuer;
        options.Events.OnRedirectToIdentityProvider = context =>
        {
            context.ProtocolMessage.RedirectUri = settings.PublicOrigin + Callback;
            return Task.CompletedTask;
        };
        options.Events.OnTokenValidated = async context =>
        {
            var subject = context.Principal?.FindFirstValue("sub");
            if (context.SecurityToken.Issuer != settings.Issuer || string.IsNullOrWhiteSpace(subject) ||
                (settings.RequiredAcr is { Length: > 0 } acr && context.Principal?.FindFirstValue("acr") != acr))
            {
                context.Fail("The external issuer or claims are not allowed.");
                return;
            }

            var key = ExternalIdentity.Key(settings.Issuer, subject);
            var users = context.HttpContext.RequestServices.GetRequiredService<IFederatedAccounts>();
            var localId = await users.FindUserAsync(key);
            if (localId is null)
            {
                context.Fail("No eligible local account is linked.");
                return;
            }

            // The protocol handler still needs the original ID token claims to validate the nonce.
            context.Properties!.Items["local-user"] = localId;
            context.Properties.Items["external-key"] = key;
        };
        options.Events.OnTicketReceived = async context =>
        {
            var properties = context.Properties!;
            var handoffs = context.HttpContext.RequestServices.GetRequiredService<IFederationHandoffs>();
            var id = await handoffs.IssueAsync(properties.Items["local-user"]!, properties.Items["external-key"]!,
                context.HttpContext.RequestAborted);
            properties.Items["handoff-id"] = id.ToString();
            context.Principal = new ClaimsPrincipal(new ClaimsIdentity([], Exchange));
        };
        options.Events.OnRemoteFailure = async context =>
        {
            context.HandleResponse();
            await Results.Problem(statusCode: 401, title: "Organization sign-in failed.").ExecuteAsync(context.HttpContext);
        };
    }

    private static string Required(string? value, string key) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException($"{key} is required.");

    private static bool IsSafeUri(Uri uri, bool allowHttp) =>
        (uri.Scheme == Uri.UriSchemeHttps || (allowHttp && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
