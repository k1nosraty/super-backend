using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SuperBackend.Platform;

namespace SuperBackend.Jobs;

public static class JobsModule
{
    public const string Inspect = "jobs.inspect";
    public const string Retry = "jobs.retry";

    public static void AddJobsModule(this IMvcBuilder mvc, string connection, IConfiguration configuration)
    {
        mvc.AddApplicationPart(typeof(JobsModule).Assembly);
        var services = mvc.Services;
        services.AddDbContext<JobsData>(options => options.UseNpgsql(connection,
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", JobsData.Schema)));
        services.AddScoped<PostgresJobQueue>();
        services.AddScoped<IJobOperations>(provider => provider.GetRequiredService<PostgresJobQueue>());
        services.AddScoped<IDurableJobQueue>(provider => provider.GetRequiredService<PostgresJobQueue>());
        services.AddAuthorizationBuilder().AddPermission(Inspect, "Inspect job status")
            .AddPermission(Retry, "Requeue a dead job", requiresMfa: true);
        services.AddHealthChecks().AddMigrationReadiness<JobsData>("jobs-db");

        var poll = configuration.GetValue("Jobs:PollMilliseconds", 500);
        var lease = configuration.GetValue("Jobs:LeaseSeconds", 60);
        var timeout = configuration.GetValue("Jobs:AttemptTimeoutSeconds", 45);
        var retry = configuration.GetValue("Jobs:RetrySeconds", 5);
        if (poll is < 50 or > 60_000 || timeout < 1 || lease <= timeout || lease > 3600 || retry is < 1 or > 300)
            throw new InvalidOperationException("Invalid queue poll, lease, timeout or retry settings.");

        services.AddSingleton(new QueueSettings(TimeSpan.FromMilliseconds(poll), TimeSpan.FromSeconds(lease),
            TimeSpan.FromSeconds(timeout), TimeSpan.FromSeconds(retry)));
        if (configuration.GetValue("Jobs:RunWorker", true))
            services.AddHostedService<JobWorker>();
    }

    public static async Task MigrateJobsAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobsData>().Database.MigrateAsync();
    }
}
