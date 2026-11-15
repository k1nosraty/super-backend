using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SuperBackend.Platform;

namespace SuperBackend.Tenancy;

public static class TenancyModule
{
    public const string Manage = "tenancy.manage";

    public static void AddTenancyModule(this IMvcBuilder mvc, string connection, string tenantConnection)
    {
        mvc.AddApplicationPart(typeof(TenancyModule).Assembly);
        var services = mvc.Services;
        services.AddDbContext<TenancyData>(options => options.UseNpgsql(connection,
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", TenancyData.Schema)));
        services.AddSingleton(new TenantDataSession(new DbContextOptionsBuilder<TenancyData>()
            .UseNpgsql(tenantConnection).Options));
        services.AddSingleton<ITenantDatabaseSession>(provider => provider.GetRequiredService<TenantDataSession>());
        services.AddScoped<TenantDirectory>();
        services.AddScoped<ITenantDirectory>(provider => provider.GetRequiredService<TenantDirectory>());
        services.AddScoped<ITenantAccess>(provider => provider.GetRequiredService<TenantDirectory>());
        services.AddScoped<ITenantDocuments, TenantDocuments>();
        services.AddAuthorizationBuilder().AddPermission(Manage, "Manage tenants and memberships");
        services.AddHealthChecks().AddCheck<TenantDataSession>("tenant-data-rls", tags: ["ready"]);
    }

    public static async Task MigrateTenancyAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TenancyData>().Database.MigrateAsync();
    }
}
