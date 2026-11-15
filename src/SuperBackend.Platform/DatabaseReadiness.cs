using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SuperBackend.Platform;

public static class DatabaseReadiness
{
    public static IHealthChecksBuilder AddMigrationReadiness<T>(this IHealthChecksBuilder checks, string name)
        where T : DbContext =>
        checks.AddDbContextCheck<T>(name, tags: ["ready"], customTestQuery: async (context, ct) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var expected = context.Database.GetMigrations().ToArray();
            if (expected.Length == 0)
                return false;
            var applied = (await context.Database.GetAppliedMigrationsAsync(timeout.Token)).ToHashSet(StringComparer.Ordinal);
            return expected.All(applied.Contains);
        });
}
