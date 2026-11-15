using Microsoft.AspNetCore.DataProtection;
using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using SuperBackend.Identity;

namespace SuperBackend.Api;

internal static class ApiSecurity
{
    public static void AddApiSecurity(this WebApplicationBuilder builder)
    {
        var development = builder.Environment.IsDevelopment();
        var origins = builder.Configuration.GetSection("Http:CorsOrigins").Get<string[]>() ?? [];
        if (origins.Any(origin => !IsOrigin(origin, development)))
            throw new InvalidOperationException("Http:CorsOrigins must contain explicit origins.");

        builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
            policy.WithOrigins(origins).AllowAnyMethod().AllowAnyHeader()));
        builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            var proxies = builder.Configuration.GetSection("Http:KnownProxies").Get<string[]>() ?? [];
            if (proxies.Length == 0)
                return;

            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var proxy in proxies)
                options.KnownProxies.Add(IPAddress.Parse(proxy));
        });

        var keys = builder.Configuration["DataProtection:KeyDirectory"];
        if (!development && string.IsNullOrWhiteSpace(keys))
            throw new InvalidOperationException("DataProtection:KeyDirectory is required outside Development.");

        var protection = builder.Services.AddDataProtection().SetApplicationName("SuperBackend");
        if (!string.IsNullOrWhiteSpace(keys))
            protection.PersistKeysToFileSystem(Directory.CreateDirectory(keys));

        var general = builder.Configuration.GetValue("RateLimits:RequestsPerMinute", 180);
        var identity = builder.Configuration.GetValue("RateLimits:IdentityRequestsPerMinute", 20);
        if (general <= 0 || identity <= 0)
            throw new InvalidOperationException("Rate limits must be positive.");

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                context => ForIp(context, general));
            options.AddPolicy(IdentityModule.RateLimit, context => ForIp(context, identity));
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Ceiling(retry.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
        });
    }

    internal static bool IsOrigin(string value, bool allowHttp) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || (allowHttp && uri.Scheme == Uri.UriSchemeHttp)) &&
        uri.GetLeftPart(UriPartial.Authority) == value;

    private static RateLimitPartition<string> ForIp(HttpContext context, int limit)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip?.IsIPv4MappedToIPv6 == true)
            ip = ip.MapToIPv4();
        return RateLimitPartition.GetFixedWindowLimiter(ip?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    }
}
