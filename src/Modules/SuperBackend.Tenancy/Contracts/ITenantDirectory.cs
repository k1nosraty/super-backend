namespace SuperBackend.Tenancy;

public interface ITenantDirectory
{
    Task<List<TenantSummary>> ListAsync(string userId, int offset, CancellationToken ct);
    Task<TenantSummary> CreateAsync(string name, string actor, CancellationToken ct);
    Task<bool> SetMembershipAsync(Guid tenantId, string userId, TenantAccess? access, string actor, CancellationToken ct);
    Task<bool> SetStatusAsync(Guid tenantId, bool active, string actor, string reason, CancellationToken ct);
}
