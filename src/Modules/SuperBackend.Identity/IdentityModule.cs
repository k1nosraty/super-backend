using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using SuperBackend.Platform;

namespace SuperBackend.Identity;

public static class IdentityModule
{
    public const string RateLimit = "identity";
    public const string AdministrationPolicy = AccessPolicies.AdministrationPolicy;

    public static void AddIdentityModule(this WebApplicationBuilder builder, string connection)
    {
        var services = builder.Services;
        services.AddDbContext<IdentityData>(options => options.UseNpgsql(connection,
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", IdentityData.Schema)));
        services.AddIdentityApiEndpoints<Account>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = true;
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole>()
            .AddSignInManager<AccountSignInManager>()
            .AddClaimsPrincipalFactory<UserClaimsPrincipalFactory<Account>>()
            .AddEntityFrameworkStores<IdentityData>();

        services.Configure<AuthenticationOptions>(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.BearerScheme;
            options.DefaultChallengeScheme = IdentityConstants.BearerScheme;
            options.DefaultForbidScheme = IdentityConstants.BearerScheme;
        });
        services.Configure<BearerTokenOptions>(IdentityConstants.BearerScheme, options =>
        {
            options.BearerTokenExpiration = TimeSpan.FromMinutes(10);
            options.RefreshTokenExpiration = TimeSpan.FromDays(7);
        });
        services.Configure<DataProtectionTokenProviderOptions>(options =>
            options.TokenLifespan = TimeSpan.FromHours(2));

        services.AddHttpContextAccessor();
        services.AddScoped<IFederatedAccounts, FederatedAccounts>();
        services.AddScoped<IAuthorizationHandler, AccessPolicies>();
        services.AddAuthorizationBuilder().AddPolicy(AccessPolicies.AdministrationPolicy,
            policy => policy.RequireAuthenticatedUser()
                .AddRequirements(new Permission(AccessPolicies.AdministrationPolicy, "Manage access")));
        services.AddOptions<SmtpOptions>().BindConfiguration("Smtp")
            .Validate(options => options.IsValid() &&
                (builder.Environment.IsDevelopment() || options.UseStartTls), "Invalid SMTP settings.")
            .ValidateOnStart();
        var origin = builder.Configuration["Identity:PublicOrigin"];
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var publicOrigin) ||
            (publicOrigin.Scheme != Uri.UriSchemeHttps &&
                (!builder.Environment.IsDevelopment() || publicOrigin.Scheme != Uri.UriSchemeHttp)) ||
            publicOrigin.GetLeftPart(UriPartial.Authority) != origin)
        {
            throw new InvalidOperationException("Identity:PublicOrigin must be an explicit origin.");
        }
        services.AddSingleton(new IdentityPublicOrigin(publicOrigin));
        services.AddSingleton<SmtpEmailSender>();
        services.AddSingleton<IEmailSender<Account>>(provider => provider.GetRequiredService<SmtpEmailSender>());
        services.AddHealthChecks().AddDbContextCheck<IdentityData>("identity-db", tags: ["ready"]);
    }

    public static void UseIdentitySessionValidation(this WebApplication app) =>
        app.UseMiddleware<ValidateSession>();

    public static void MapIdentityModule(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        var auth = api.MapGroup("/auth").WithTags("Identity").RequireRateLimiting(RateLimit)
            .AddEndpointFilter(ValidateIdentityBody);
        var allowRegistration = app.Configuration.GetValue<bool>("Identity:AllowRegistration");
        auth.MapIdentityApi<Account>().Add(endpoint =>
        {
            var route = ((RouteEndpointBuilder)endpoint).RoutePattern.RawText;
            var methods = endpoint.Metadata.OfType<HttpMethodMetadata>().Single().HttpMethods;
            var post = methods.Contains(HttpMethods.Post);
            if (route is not null && !route.StartsWith("/api/v1/auth/manage/", StringComparison.Ordinal))
                endpoint.Metadata.Add(new AllowAnonymousAttribute());

            if (!allowRegistration && post && route == "/api/v1/auth/register")
            {
                endpoint.RequestDelegate = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                };
                endpoint.Metadata.Add(new ExcludeFromDescriptionAttribute());
            }

            if (post && route == "/api/v1/auth/login")
            {
                var handler = endpoint.RequestDelegate!;
                endpoint.RequestDelegate = context =>
                {
                    var query = context.Request.Query;
                    if (query.ContainsKey("useCookies") || query.ContainsKey("useSessionCookies"))
                        return Results.BadRequest().ExecuteAsync(context);
                    return handler(context);
                };
            }

            if (post && route is "/api/v1/auth/manage/info" or "/api/v1/auth/manage/2fa")
            {
                var handler = endpoint.RequestDelegate!;
                endpoint.RequestDelegate = async context =>
                {
                    var password = context.Request.Headers["X-Current-Password"];
                    var users = context.RequestServices.GetRequiredService<UserManager<Account>>();
                    var user = await users.GetUserAsync(context.User);
                    if (password.Count != 1 || string.IsNullOrEmpty(password[0]) || user is null ||
                        !await users.CheckPasswordAsync(user, password[0]!))
                    {
                        await Results.Problem(statusCode: 403, title: context.RequestServices.GetRequiredService<IStringLocalizer<BackendText>>()["CurrentPasswordRequired"])
                            .ExecuteAsync(context);
                        return;
                    }
                    await handler(context);
                };
            }
        });
        auth.MapPost("/logout-all", async (ClaimsPrincipal principal, UserManager<Account> users) =>
        {
            var user = await users.GetUserAsync(principal);
            return user is null ? Results.Unauthorized() : IdentityResponse.Empty(await users.UpdateSecurityStampAsync(user));
        }).RequireAuthorization();

        ProfileEndpoints.Map(api);
        AccountInvitations.Map(AdminEndpoints.Map(api), auth);
    }

    private static ValueTask<object?> ValidateIdentityBody(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var hasMissingField = context.Arguments.Any(argument => argument switch
        {
            RegisterRequest request => string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password),
            LoginRequest request => string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password),
            RefreshRequest request => string.IsNullOrWhiteSpace(request.RefreshToken),
            ResendConfirmationEmailRequest request => string.IsNullOrWhiteSpace(request.Email),
            ForgotPasswordRequest request => string.IsNullOrWhiteSpace(request.Email),
            ResetPasswordRequest request => string.IsNullOrWhiteSpace(request.Email) ||
                string.IsNullOrWhiteSpace(request.ResetCode) || string.IsNullOrEmpty(request.NewPassword),
            _ => false
        });

        return hasMissingField
            ? ValueTask.FromResult<object?>(Results.Problem(statusCode: 400, title: context.HttpContext.RequestServices.GetRequiredService<IStringLocalizer<BackendText>>()["RequiredField"]))
            : next(context);
    }

    public static async Task MigrateIdentityAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityData>().Database.MigrateAsync();
    }

    public static async Task BootstrapAdministratorAsync(this IServiceProvider services, string email, string password)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityData>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (await users.FindByEmailAsync(email) is not null)
            throw new InvalidOperationException("An account with that email already exists.");

        var administrator = await roles.FindByNameAsync(AccessPolicies.Administrator);
        if (administrator is null)
        {
            administrator = new IdentityRole(AccessPolicies.Administrator);
            RequireSuccess(await roles.CreateAsync(administrator));
        }

        var user = new Account { Email = email, UserName = email, EmailConfirmed = true };
        RequireSuccess(await users.CreateAsync(user, password));
        RequireSuccess(await users.AddToRoleAsync(user, AccessPolicies.Administrator));
        db.AccessAudits.Add(new AccessAudit
        {
            ActorId = "operator",
            Action = "administrator.provisioned",
            UserId = user.Id,
            RoleId = administrator.Id,
            OccurredAt = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow()
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static void RequireSuccess(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(error => error.Code)));
    }
}
