using Microsoft.EntityFrameworkCore;

namespace SuperBackend.Outbox;

internal sealed class OutboxData(DbContextOptions<OutboxData> options) : DbContext(options)
{
    public const string Schema = "outbox";
    public DbSet<OutboxMessage> Messages => Set<OutboxMessage>();
    public DbSet<InboxReceipt> InboxReceipts => Set<InboxReceipt>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<OutboxMessage>(entity =>
        {
            entity.Property(message => message.Type).HasMaxLength(100);
            entity.Property(message => message.Payload).HasColumnType("jsonb");
            entity.HasIndex(message => new { message.CreatedAt, message.Id })
                .HasFilter("""
                    "DispatchedAt" IS NULL AND "ConflictAt" IS NULL
                    """);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Messages_MaxAttempts", """
                    "MaxAttempts" BETWEEN 1 AND 20
                    """);
                table.HasCheckConstraint("CK_Messages_TerminalState", """
                    "DispatchedAt" IS NULL OR "ConflictAt" IS NULL
                    """);
            });
        });
        model.Entity<InboxReceipt>(entity =>
        {
            entity.HasKey(receipt => new { receipt.Consumer, receipt.MessageId });
            entity.Property(receipt => receipt.Consumer).HasMaxLength(100);
        });
    }
}
