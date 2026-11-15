using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SuperBackend.Identity;

internal sealed class Account : IdentityUser
{
    public string? DisplayName { get; set; }
    public bool IsDisabled { get; set; }
}

internal sealed record RolePermission(string RoleId, string Name);

internal sealed class AccessAudit
{
    public long Id { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string ActorId { get; init; }
    public required string Action { get; init; }
    public string? RoleId { get; init; }
    public string? UserId { get; init; }
    public string? Permission { get; init; }
    public string? Reason { get; init; }
    public string? TraceId { get; init; }
}

internal sealed class IdentityData(DbContextOptions<IdentityData> options)
    : IdentityDbContext<Account>(options)
{
    public const string Schema = "identity";
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<AccountSession> Sessions => Set<AccountSession>();
    public DbSet<FederationHandoff> FederationHandoffs => Set<FederationHandoff>();
    public DbSet<AccessAudit> AccessAudits => Set<AccessAudit>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.HasDefaultSchema(Schema);
        model.Entity<AccountSession>(entity =>
        {
            entity.HasIndex(session => session.UserId);
            entity.HasIndex(session => session.ExpiresAt);
            entity.HasOne<Account>().WithMany().HasForeignKey(session => session.UserId);
        });
        model.Entity<FederationHandoff>().HasIndex(handoff => handoff.ExpiresAt);
        model.Entity<Account>(entity =>
        {
            entity.Property(user => user.DisplayName).HasMaxLength(100);
            entity.HasIndex(user => user.NormalizedEmail).IsUnique().HasDatabaseName("EmailIndex");
        });
        model.Entity<RolePermission>(entity =>
        {
            entity.HasKey(grant => new { grant.RoleId, grant.Name });
            entity.Property(grant => grant.Name).HasMaxLength(120);
            entity.HasOne<IdentityRole>().WithMany().HasForeignKey(grant => grant.RoleId);
        });
    }
}
