using System.Diagnostics;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.WebUtilities;
using SuperBackend.Platform;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using SuperBackend.Api;
using SuperBackend.Identity;
using SuperBackend.Federation;
using SuperBackend.Jobs;
using SuperBackend.Outbox;
using SuperBackend.Tenancy;
using SuperBackend.Notes;

var maintenance = args is ["--maintenance"];
var verifyProfile = args is ["--check-profile"];
var migrate = args is ["--migrate"];
var recover = args is ["--recover-admin", _, _];
var bootstrap = args is ["--bootstrap-admin", _];
if (!migrate && !bootstrap && !recover && !maintenance && !verifyProfile && args.Any(arg => arg is "--migrate" or "--bootstrap-admin" or "--recover-admin" or "--maintenance" or "--check-profile"))
    throw new ArgumentException("Use --check-profile, --migrate, --maintenance, --bootstrap-admin EMAIL, or --recover-admin EMAIL ACTION.");

var builder = WebApplication.CreateBuilder(migrate || bootstrap || recover || maintenance || verifyProfile ? [] : args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024);
var connection = builder.Configuration.GetConnectionString("Main")
    ?? throw new InvalidOperationException("ConnectionStrings:Main is required.");
var development = builder.Environment.IsDevelopment();
var features = FeatureProfile.Read(builder.Configuration);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddApiLocalization();
builder.AddObservability();
builder.Services.AddOpenApi(ApiOpenApi.Configure);
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        var text = context.HttpContext.RequestServices.GetRequiredService<IStringLocalizer<BackendText>>();
        var status = context.ProblemDetails.Status;
        if (status is int code && !text[$"Http{code}"].ResourceNotFound &&
            (context.ProblemDetails.Title is null || context.ProblemDetails.Title == ReasonPhrases.GetReasonPhrase(code) ||
                context.ProblemDetails.Title == "An error occurred while processing your request."))
        {
            context.ProblemDetails.Title = text[$"Http{code}"];
        }

        context.ProblemDetails.Extensions.TryAdd("code", $"http_{status ?? 500}");
        context.ProblemDetails.Extensions["traceId"] =
                Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
    });
builder.Services.AddExceptionHandler<PersistenceErrors>();
builder.AddApiSecurity();
builder.AddIdentityModule(connection);
if (features.Notes)
    builder.Services.AddNotesModule(connection);

var mvc = builder.Services.AddControllers().ConfigureApplicationPartManager(manager => manager.ApplicationParts.Clear());
if (features.Federation)
    builder.AddFederationModule(mvc);
if (features.Tenancy)
    mvc.AddTenancyModule(connection, builder.Configuration.GetConnectionString("TenantData") ?? connection);
if (features.Jobs)
    mvc.AddJobsModule(connection, builder.Configuration);
if (features.Outbox)
    builder.Services.AddOutboxModule(connection, builder.Configuration);
if (features.Notes && features.Outbox)
    builder.Services.AddNotesOutbox(features.PublishNoteJobs);
ProductModules.Register(builder, mvc);

var app = builder.Build();

if (verifyProfile)
{
    Console.WriteLine("The configured feature profile is valid.");
    return;
}

if (migrate)
{
    await app.Services.MigrateIdentityAsync();
    if (features.Notes)
        await app.Services.MigrateNotesAsync();
    if (features.Tenancy)
        await app.Services.MigrateTenancyAsync();
    if (features.Jobs)
        await app.Services.MigrateJobsAsync();
    if (features.Outbox)
        await app.Services.MigrateOutboxAsync();
    return;
}

if (bootstrap)
{
    var password = builder.Configuration["Bootstrap:Password"]
        ?? throw new InvalidOperationException("Configure Bootstrap__Password before provisioning an administrator.");
    await app.Services.BootstrapAdministratorAsync(args[1], password);
    return;
}

if (recover)
{
    await app.Services.RecoverAdministratorAsync(args[1], builder.Configuration["Recovery:Operator"] ?? "",
        builder.Configuration["Recovery:Reason"] ?? "", args[2], builder.Configuration["Recovery:Password"]);
    return;
}

if (maintenance)
{
    await RetentionMaintenance.RunAsync(connection, features, builder.Configuration.GetValue("Retention:FinishedDays", 30),
        builder.Configuration.GetValue("Retention:BatchSize", 1000));
    return;
}

app.UseForwardedHeaders();
app.UseRequestLocalization();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!development)
    app.UseHsts();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path.StartsWithSegments("/admin"))
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
    if (!development && !context.Request.IsHttps)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    await next(context);
});

app.UseRouting();
app.Use(async (context, next) =>
{
    // Routing's framework-generated rejection has no authorization metadata.
    if (context.GetEndpoint() is { DisplayName: "415 HTTP Unsupported Media Type" })
    {
        context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
        return;
    }

    await next(context);
});
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseIdentitySessionValidation();
app.UseAuthorization();

if (development)
    app.MapOpenApi().AllowAnonymous();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous().DisableRateLimiting();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous().DisableRateLimiting();

app.MapAdminPanel();
app.MapIdentityModule();
app.MapControllers();
if (features.Outbox)
    app.MapOutboxOperations();
if (features.Notes)
    app.MapNotesModule();

await app.RunAsync();

public partial class Program;
