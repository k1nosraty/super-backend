namespace SuperBackend.Tenancy;

public interface ITenantDirectory
{
    Task<List<TenantSummary>> ListAsync(string userId, int offset, CancellationToken ct);
    Task<TenantSummary> CreateAsync(string name, CancellationToken ct);
    Task<bool> SetMembershipAsync(Guid tenantId, string userId, TenantAccess? access, CancellationToken ct);
}
