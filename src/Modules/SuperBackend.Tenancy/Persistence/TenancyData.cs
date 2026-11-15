using Microsoft.EntityFrameworkCore;

namespace SuperBackend.Tenancy;

internal sealed record Tenant(Guid Id, string Name)
{
    public bool IsActive { get; init; } = true;
}
internal sealed record Membership(Guid TenantId, string UserId, TenantAccess Access);

internal sealed class Document
{
    public Guid TenantId { get; init; }
    public Guid Id { get; init; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public Guid Version { get; set; }
}

internal sealed class TenancyData(DbContextOptions<TenancyData> options) : DbContext(options)
{
    public const string Schema = "tenancy";
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Document> Documents => Set<Document>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<Tenant>().Property(item => item.IsActive).HasDefaultValue(true);
        model.Entity<Tenant>().Property(item => item.Name).HasMaxLength(100);
        model.Entity<Membership>(entity =>
        {
            entity.HasKey(item => new { item.TenantId, item.UserId });
            entity.Property(item => item.UserId).HasMaxLength(128);
            entity.HasIndex(item => new { item.UserId, item.TenantId });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(item => item.TenantId);
        });
        model.Entity<Document>(entity =>
        {
            entity.HasKey(item => new { item.TenantId, item.Id });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(item => item.TenantId);
            entity.Property(item => item.Title).HasMaxLength(120);
            entity.Property(item => item.Version).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(item => item.Body).HasMaxLength(4000);
        });
    }
}
