using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SuperBackend.Tenancy;

internal sealed class TenantDataSession(DbContextOptions<TenancyData> options) : ITenantDatabaseSession, IHealthCheck
{
    private const string Documents = "tenancy.\"Documents\"";

    internal Task<T> ExecuteAsync<T>(Guid tenantId, Func<TenancyData, CancellationToken, Task<T>> operation,
        CancellationToken ct) =>
        ExecuteCoreAsync(tenantId, Documents, (data, _, token) => operation(data, token), ct);

    public Task<T> ExecuteAsync<T>(Guid tenantId, string protectedRelation,
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> operation, CancellationToken ct) =>
        ExecuteCoreAsync(tenantId, protectedRelation, (data, tx, token) =>
            operation(data.Database.GetDbConnection(), tx.GetDbTransaction(), token), ct);

    private async Task<T> ExecuteCoreAsync<T>(Guid tenantId, string protectedRelation,
        Func<TenancyData, IDbContextTransaction, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        await using var data = new TenancyData(options);
        await using var transaction = await data.Database.BeginTransactionAsync(ct);
        if (!await RowSecurityActiveAsync(data, protectedRelation, ct))
            throw new InvalidOperationException("Tenant data requires an active PostgreSQL row-security policy for the runtime role.");

        await data.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('superbackend.tenant_id', {tenantId.ToString()}, true)", ct);
        var result = await operation(data, transaction, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        await using var data = new TenancyData(options);
        return await RowSecurityActiveAsync(data, Documents, cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Row security is inactive for the tenant runtime database role.");
    }

    private static Task<bool> RowSecurityActiveAsync(TenancyData data, string protectedRelation, CancellationToken ct) =>
        data.Database.SqlQuery<bool>(
            $"""SELECT coalesce(row_security_active(to_regclass({protectedRelation})), false) AS "Value" """).SingleAsync(ct);
}
