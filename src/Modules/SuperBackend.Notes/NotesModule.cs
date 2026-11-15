using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SuperBackend.Jobs;
using SuperBackend.Platform;

namespace SuperBackend.Notes;

public static class NotesModule
{
    private const string Read = "notes.read";
    private const string Write = "notes.write";

    public static void AddNotesModule(this IServiceCollection services, string connection)
    {
        services.AddDbContext<NotesData>(options => options.UseNpgsql(connection,
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", NotesData.Schema)));
        services.AddAuthorizationBuilder()
            .AddPermission(Read, "Read your own notes")
            .AddPermission(Write, "Create, edit and delete your own notes");
        services.AddHealthChecks().AddDbContextCheck<NotesData>("notes-db", tags: ["ready"]);
    }

    public static void AddNotesOutbox(this IServiceCollection services, bool publishCreatedJobs)
    {
        services.AddScoped<IDurableJobHandler, NoteCreatedHandler>();
        if (publishCreatedJobs)
            services.AddScoped<NoteOutboxWriter>();
    }

    public static void MapNotesModule(this WebApplication app)
    {
        var notes = app.MapGroup("/api/v1/notes").WithTags("Notes");
        notes.MapGet("/", ListAsync).RequireAuthorization(Read);
        notes.MapGet("/{id:guid}", GetAsync).WithName("GetNote").RequireAuthorization(Read);
        notes.MapPost("/", CreateAsync).RequireAuthorization(Write).ValidateBody<NoteInput>();
        notes.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(Write).ValidateBody<UpdateNote>();
        notes.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(Write);
    }

    public static async Task MigrateNotesAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NotesData>().Database.MigrateAsync();
    }

    private static Task<List<NoteView>> ListAsync(int? offset, int? limit, ClaimsPrincipal user, NotesData db,
        CancellationToken ct) =>
        OwnedNotes(user, db).OrderByDescending(note => note.CreatedAt).ThenByDescending(note => note.Id)
            .Skip(Math.Max(offset ?? 0, 0)).Take(Math.Clamp(limit ?? 25, 1, 100))
            .Select(note => new NoteView(note.Id, note.Title, note.Body, note.CreatedAt, note.Version)).ToListAsync(ct);

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, NotesData db, CancellationToken ct)
    {
        var note = await OwnedNotes(user, db).SingleOrDefaultAsync(note => note.Id == id, ct);
        return note is null ? Results.NotFound() : Results.Ok(ToView(note));
    }

    private static async Task<IResult> CreateAsync(NoteInput input, ClaimsPrincipal user, NotesData db,
        TimeProvider time, [FromServices] NoteOutboxWriter? outbox, CancellationToken ct)
    {
        var note = new Note
        {
            Id = Guid.NewGuid(),
            OwnerId = user.UserId(),
            Title = input.Title.Trim(),
            Body = input.Body,
            CreatedAt = time.GetUtcNow(),
            Version = Guid.NewGuid()
        };
        db.Notes.Add(note);
        if (outbox is null)
            await db.SaveChangesAsync(ct);
        else
            await outbox.SaveCreatedAsync(note, ct);

        return Results.CreatedAtRoute("GetNote", new { note.Id }, ToView(note));
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateNote input, ClaimsPrincipal user, NotesData db,
        CancellationToken ct)
    {
        var note = await OwnedNotes(user, db).SingleOrDefaultAsync(note => note.Id == id, ct);
        if (note is null)
            return Results.NotFound();
        if (note.Version != input.Version)
            return Results.Conflict();

        note.Title = input.Title.Trim();
        note.Body = input.Body;
        note.Version = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToView(note));
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal user, NotesData db, CancellationToken ct)
    {
        var count = await OwnedNotes(user, db).Where(note => note.Id == id).ExecuteDeleteAsync(ct);
        return count == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static IQueryable<Note> OwnedNotes(ClaimsPrincipal user, NotesData db)
    {
        var ownerId = user.UserId();
        return db.Notes.Where(note => note.OwnerId == ownerId);
    }

    private static NoteView ToView(Note note) => new(note.Id, note.Title, note.Body, note.CreatedAt, note.Version);

    private sealed record NoteView(Guid Id, string Title, string Body, DateTimeOffset CreatedAt, Guid Version);
}

internal sealed record NoteInput(
    [property: Required, StringLength(120)] string Title,
    [property: Required(AllowEmptyStrings = true), StringLength(4000)] string Body);

internal sealed record UpdateNote(
    [property: Required, StringLength(120)] string Title,
    [property: Required(AllowEmptyStrings = true), StringLength(4000)] string Body,
    Guid Version);
