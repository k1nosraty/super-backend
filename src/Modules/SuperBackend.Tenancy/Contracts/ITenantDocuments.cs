namespace SuperBackend.Tenancy;

public interface ITenantDocuments
{
    Task<List<TenantDocument>> ListAsync(Guid tenantId, int offset, CancellationToken ct);
    Task<TenantDocument?> FindAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<TenantDocument> CreateAsync(Guid tenantId, DocumentInput input, CancellationToken ct);
    Task<bool> UpdateAsync(Guid tenantId, Guid id, DocumentInput input, CancellationToken ct);
    Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct);
}
