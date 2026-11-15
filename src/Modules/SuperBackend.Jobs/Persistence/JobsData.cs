using Microsoft.EntityFrameworkCore;

namespace SuperBackend.Jobs;

internal sealed class JobsData(DbContextOptions<JobsData> options) : DbContext(options)
{
    public const string Schema = "jobs";
    public DbSet<Job> Jobs => Set<Job>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<Job>(entity =>
        {
            entity.Property(item => item.Type).HasMaxLength(100);
            entity.Property(item => item.Status).HasMaxLength(16);
            entity.Property(item => item.Payload).HasColumnType("jsonb");
            entity.Property(item => item.LastError).HasMaxLength(64);
            entity.HasIndex(item => new { item.Status, item.AvailableAt, item.Id });
            entity.HasIndex(item => new { item.Status, item.LeaseUntil });
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Jobs_Status",
                    """
                    "Status" IN ('pending','running','succeeded','dead')
                    """);
                table.HasCheckConstraint("CK_Jobs_Attempts",
                    """
                    "Attempts" >= 0 AND "MaxAttempts" BETWEEN 1 AND 20 AND "Attempts" <= "MaxAttempts"
                    """);
            });
        });
    }
}
