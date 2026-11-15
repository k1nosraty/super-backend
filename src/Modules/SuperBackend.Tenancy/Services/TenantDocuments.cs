using Microsoft.EntityFrameworkCore;

namespace SuperBackend.Tenancy;

internal sealed class TenantDocuments(TenantDataSession session) : ITenantDocuments
{
    public Task<List<TenantDocument>> ListAsync(Guid tenantId, int offset, CancellationToken ct) =>
        session.ExecuteAsync(tenantId, (data, token) => data.Documents.Where(item => item.TenantId == tenantId)
            .OrderBy(item => item.Id).Skip(Math.Max(0, offset)).Take(50)
            .Select(item => new TenantDocument(item.Id, item.Title, item.Body)).ToListAsync(token), ct);

    public Task<TenantDocument?> FindAsync(Guid tenantId, Guid id, CancellationToken ct) =>
        session.ExecuteAsync(tenantId, (data, token) => Match(data, tenantId, id)
            .Select(item => new TenantDocument(item.Id, item.Title, item.Body)).SingleOrDefaultAsync(token), ct);

    public Task<TenantDocument> CreateAsync(Guid tenantId, DocumentInput input, CancellationToken ct) =>
        session.ExecuteAsync(tenantId, async (data, token) =>
        {
            var document = new Document { TenantId = tenantId, Id = Guid.NewGuid(), Title = input.Title.Trim(), Body = input.Body };
            data.Documents.Add(document);
            await data.SaveChangesAsync(token);
            return new TenantDocument(document.Id, document.Title, document.Body);
        }, ct);

    public Task<bool> UpdateAsync(Guid tenantId, Guid id, DocumentInput input, CancellationToken ct) =>
        session.ExecuteAsync(tenantId, async (data, token) =>
            await Match(data, tenantId, id).ExecuteUpdateAsync(update => update
                .SetProperty(item => item.Title, input.Title.Trim()).SetProperty(item => item.Body, input.Body), token) != 0, ct);

    public Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct) =>
        session.ExecuteAsync(tenantId, async (data, token) =>
            await Match(data, tenantId, id).ExecuteDeleteAsync(token) != 0, ct);

    private static IQueryable<Document> Match(TenancyData data, Guid tenantId, Guid id) =>
        data.Documents.Where(item => item.TenantId == tenantId && item.Id == id);
}
