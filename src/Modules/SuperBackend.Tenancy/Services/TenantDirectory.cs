using Microsoft.EntityFrameworkCore;
using SuperBackend.Platform;

namespace SuperBackend.Tenancy;

internal sealed class TenantDirectory(TenancyData data, IAccountDirectory accounts) : ITenantAccess, ITenantDirectory
{
    public Task<bool> AllowsAsync(Guid tenantId, string userId, bool write, CancellationToken ct) =>
        data.Memberships.AnyAsync(item => item.TenantId == tenantId && item.UserId == userId &&
            (!write || item.Access == TenantAccess.Editor) && data.Tenants.Any(tenant => tenant.Id == tenantId && tenant.IsActive), ct);

    public Task<List<TenantSummary>> ListAsync(string userId, int offset, CancellationToken ct) =>
        data.Memberships.Where(item => item.UserId == userId)
            .Join(data.Tenants.Where(tenant => tenant.IsActive), membership => membership.TenantId, tenant => tenant.Id,
                (_, tenant) => tenant)
            .OrderBy(item => item.Name).ThenBy(item => item.Id)
            .Skip(Math.Max(0, offset)).Take(50)
            .Select(item => new TenantSummary(item.Id, item.Name)).ToListAsync(ct);

    public async Task<TenantSummary> CreateAsync(string name, string actor, CancellationToken ct)
    {
        var tenant = new Tenant(Guid.NewGuid(), name.Trim());
        await using var transaction = await data.Database.BeginTransactionAsync(ct);
        data.Tenants.Add(tenant);
        await data.SaveChangesAsync(ct);
        await OperationsAudit.RecordAsync(data, actor, "tenant.created", tenant.Id.ToString(), "Operator created tenant.", ct);
        await transaction.CommitAsync(ct);
        return new TenantSummary(tenant.Id, tenant.Name);
    }

    public async Task<bool> SetMembershipAsync(Guid tenantId, string userId, TenantAccess? access, string actor, CancellationToken ct)
    {
        if (!await data.Tenants.AnyAsync(item => item.Id == tenantId, ct) ||
            (access is not null && !await accounts.ExistsAsync(userId, ct)))
            return false;

        await using var transaction = await data.Database.BeginTransactionAsync(ct);
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
        await OperationsAudit.RecordAsync(data, actor, access is null ? "tenant.member.removed" : "tenant.member.set",
            $"{tenantId}/{userId}", access?.ToString() ?? "Operator removed membership.", ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> SetStatusAsync(Guid tenantId, bool active, string actor, string reason, CancellationToken ct)
    {
        await using var transaction = await data.Database.BeginTransactionAsync(ct);
        var changed = await data.Tenants.Where(item => item.Id == tenantId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.IsActive, active), ct);
        if (changed == 0)
            return false;
        await OperationsAudit.RecordAsync(data, actor, active ? "tenant.reactivated" : "tenant.suspended",
            tenantId.ToString(), reason, ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
