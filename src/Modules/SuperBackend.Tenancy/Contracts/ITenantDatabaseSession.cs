using System.Data.Common;

namespace SuperBackend.Tenancy;

public interface ITenantDatabaseSession
{
    Task<T> ExecuteAsync<T>(Guid tenantId, string protectedRelation,
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> operation, CancellationToken ct);
}
