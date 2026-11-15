using Microsoft.EntityFrameworkCore;

namespace SuperBackend.Notes;

internal sealed class Note
{
    public Guid Id { get; init; }
    public required string OwnerId { get; init; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public Guid Version { get; set; }
}

internal sealed record NoteCreationStats(string OwnerId, long TotalCreated);

internal sealed class NotesData(DbContextOptions<NotesData> options) : DbContext(options)
{
    public const string Schema = "notes";
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<NoteCreationStats>(entity =>
        {
            entity.ToTable("NoteCreationStats");
            entity.HasKey(stats => stats.OwnerId);
            entity.Property(stats => stats.OwnerId).HasMaxLength(128);
        });
        model.Entity<Note>(entity =>
        {
            entity.HasIndex(note => new { note.OwnerId, note.CreatedAt, note.Id });
            entity.Property(note => note.Title).HasMaxLength(120);
            entity.Property(note => note.Body).HasMaxLength(4000);
            entity.Property(note => note.Version).IsConcurrencyToken();
        });
    }
}
