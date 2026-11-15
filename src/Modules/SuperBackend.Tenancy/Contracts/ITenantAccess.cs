namespace SuperBackend.Tenancy;

public interface ITenantAccess
{
    Task<bool> AllowsAsync(Guid tenantId, string userId, bool write, CancellationToken ct);
}
