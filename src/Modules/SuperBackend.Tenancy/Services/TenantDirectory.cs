using Microsoft.EntityFrameworkCore;

namespace SuperBackend.Tenancy;

internal sealed class TenantDirectory(TenancyData data) : ITenantAccess, ITenantDirectory
{
    public Task<bool> AllowsAsync(Guid tenantId, string userId, bool write, CancellationToken ct) =>
        data.Memberships.AnyAsync(item => item.TenantId == tenantId && item.UserId == userId &&
            (!write || item.Access == TenantAccess.Editor), ct);

    public Task<List<TenantSummary>> ListAsync(string userId, int offset, CancellationToken ct) =>
        data.Memberships.Where(item => item.UserId == userId)
            .Join(data.Tenants, membership => membership.TenantId, tenant => tenant.Id,
                (_, tenant) => tenant)
            .OrderBy(item => item.Name).ThenBy(item => item.Id)
            .Skip(Math.Max(0, offset)).Take(50)
            .Select(item => new TenantSummary(item.Id, item.Name)).ToListAsync(ct);

    public async Task<TenantSummary> CreateAsync(string name, CancellationToken ct)
    {
        var tenant = new Tenant(Guid.NewGuid(), name.Trim());
        data.Tenants.Add(tenant);
        await data.SaveChangesAsync(ct);
        return new TenantSummary(tenant.Id, tenant.Name);
    }

    public async Task<bool> SetMembershipAsync(Guid tenantId, string userId, TenantAccess? access, CancellationToken ct)
    {
        if (!await data.Tenants.AnyAsync(item => item.Id == tenantId, ct))
            return false;

        if (access is TenantAccess level)
        {
            await data.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO tenancy."Memberships" ("TenantId", "UserId", "Access")
                VALUES ({tenantId}, {userId}, {(int)level})
                ON CONFLICT ("TenantId", "UserId") DO UPDATE SET "Access" = EXCLUDED."Access"
                """, ct);
        }
        else
        {
            await data.Memberships.Where(item => item.TenantId == tenantId && item.UserId == userId)
                .ExecuteDeleteAsync(ct);
        }
        return true;
    }
}
