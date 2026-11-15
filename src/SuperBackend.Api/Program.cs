using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using SuperBackend.Api;
using SuperBackend.Identity;
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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOpenApi(ApiOpenApi.Configure);
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<PersistenceErrors>();
builder.AddApiSecurity();
builder.AddIdentityModule(connection);
if (notesEnabled)
    builder.Services.AddNotesModule(connection);

var app = builder.Build();

if (migrate)
{
    await app.Services.MigrateIdentityAsync();
    if (notesEnabled)
        await app.Services.MigrateNotesAsync();
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
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!development)
    app.UseHsts();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
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

app.MapIdentityModule();
if (notesEnabled)
    app.MapNotesModule();

await app.RunAsync();

public partial class Program;
