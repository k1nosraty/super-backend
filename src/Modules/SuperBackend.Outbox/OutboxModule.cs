using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SuperBackend.Platform;

namespace SuperBackend.Outbox;

public static class OutboxModule
{
    public static void AddOutboxModule(this IServiceCollection services, string connection, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Modules:Jobs"))
            throw new InvalidOperationException("Modules:Outbox requires Modules:Jobs.");

        services.AddDbContext<OutboxData>(options => options.UseNpgsql(connection,
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", OutboxData.Schema)));
        services.AddScoped<PostgresOutboxRelay>();
        services.AddAuthorizationBuilder().AddPermission(OutboxOperations.Inspect, "Inspect outbox status")
            .AddPermission(OutboxOperations.Redrive, "Resolve an outbox conflict", requiresMfa: true);
        services.AddSingleton<TransactionalMessages>();
        services.AddSingleton<ITransactionalOutbox>(provider => provider.GetRequiredService<TransactionalMessages>());
        services.AddSingleton<IJobInbox>(provider => provider.GetRequiredService<TransactionalMessages>());
        services.AddHealthChecks().AddMigrationReadiness<OutboxData>("outbox-store");

        var poll = configuration.GetValue("Outbox:PollMilliseconds", 1000);
        var batch = configuration.GetValue("Outbox:BatchSize", 100);
        if (poll is < 100 or > 60000 || batch is < 1 or > 1000)
            throw new InvalidOperationException("Invalid outbox poll interval or batch size.");

        services.AddSingleton(new RelaySettings(batch, TimeSpan.FromMilliseconds(poll)));
        if (configuration.GetValue("Outbox:RunRelay", true))
            services.AddHostedService<OutboxWorker>();
    }

    public static async Task MigrateOutboxAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OutboxData>().Database.MigrateAsync();
    }
}
