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

var migrate = args is ["--migrate"];
var bootstrap = args is ["--bootstrap-admin", _];
if (!migrate && !bootstrap && args.Any(arg => arg is "--migrate" or "--bootstrap-admin"))
    throw new ArgumentException("Use --migrate or --bootstrap-admin EMAIL as the only command.");

var builder = WebApplication.CreateBuilder(migrate || bootstrap ? [] : args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024);
var connection = builder.Configuration.GetConnectionString("Main")
    ?? throw new InvalidOperationException("ConnectionStrings:Main is required.");
var notesEnabled = builder.Configuration.GetValue<bool>("Modules:Notes");
var development = builder.Environment.IsDevelopment();
var federationEnabled = builder.Configuration.GetValue<bool>("Modules:Federation");
var tenancyEnabled = builder.Configuration.GetValue<bool>("Modules:Tenancy");
var jobsEnabled = builder.Configuration.GetValue<bool>("Modules:Jobs");
var outboxEnabled = builder.Configuration.GetValue<bool>("Modules:Outbox");
var publishNoteJobs = builder.Configuration.GetValue<bool>("Notes:PublishCreatedJobs");
if (publishNoteJobs && (!notesEnabled || !jobsEnabled || !outboxEnabled))
    throw new InvalidOperationException("Notes:PublishCreatedJobs requires Modules:Notes, Modules:Jobs and Modules:Outbox.");

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

        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
    });
builder.Services.AddExceptionHandler<PersistenceErrors>();
builder.AddApiSecurity();
builder.AddIdentityModule(connection);
if (notesEnabled)
    builder.Services.AddNotesModule(connection);

var mvc = builder.Services.AddControllers().ConfigureApplicationPartManager(manager => manager.ApplicationParts.Clear());
if (federationEnabled)
    builder.AddFederationModule(mvc);
if (tenancyEnabled)
    mvc.AddTenancyModule(connection, builder.Configuration.GetConnectionString("TenantData") ?? connection);
if (jobsEnabled)
    mvc.AddJobsModule(connection, builder.Configuration);
if (outboxEnabled)
    builder.Services.AddOutboxModule(connection, builder.Configuration);
if (notesEnabled && outboxEnabled)
    builder.Services.AddNotesOutbox(publishNoteJobs);
ProductModules.Register(builder, mvc);

var app = builder.Build();

if (migrate)
{
    await app.Services.MigrateIdentityAsync();
    if (notesEnabled)
        await app.Services.MigrateNotesAsync();
    if (tenancyEnabled)
        await app.Services.MigrateTenancyAsync();
    if (jobsEnabled)
        await app.Services.MigrateJobsAsync();
    if (outboxEnabled)
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
if (notesEnabled)
    app.MapNotesModule();

await app.RunAsync();

public partial class Program;
