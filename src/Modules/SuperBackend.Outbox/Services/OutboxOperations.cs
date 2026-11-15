using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SuperBackend.Platform;

namespace SuperBackend.Outbox;

public static class OutboxOperations
{
    public const string Inspect = "outbox.inspect";
    public const string Redrive = "outbox.redrive";

    public static void MapOutboxOperations(this WebApplication app)
    {
        var endpoints = app.MapGroup("/api/v1/admin/outbox").WithTags("Outbox");
        endpoints.MapGet("/", ListAsync).RequireAuthorization(Inspect).Produces<List<MessageSummary>>();
        endpoints.MapPost("/{id:guid}/redrive", RedriveAsync).RequireAuthorization(Redrive)
            .ValidateBody<ConflictRedrive>().Produces(202).ProducesProblem(409);
    }

    private static async Task<IResult> ListAsync(Guid? id, string? status, DateTimeOffset? before, OutboxData data,
        CancellationToken ct)
    {
        if (status is not (null or "pending" or "dispatched" or "conflict"))
            return Results.BadRequest();

        var cutoff = before?.ToUniversalTime();
        var rows = await data.Messages.Where(row => (id == null || row.Id == id) &&
                (cutoff == null || row.CreatedAt < cutoff) &&
                (status == null ||
                 (status == "pending" && row.DispatchedAt == null && row.ConflictAt == null) ||
                 (status == "dispatched" && row.DispatchedAt != null) ||
                 (status == "conflict" && row.ConflictAt != null)))
            .OrderByDescending(message => message.CreatedAt).ThenBy(message => message.Id).Take(100)
            .Select(message => new MessageSummary(message.Id, message.Type, message.CreatedAt,
                message.DispatchedAt, message.ConflictAt, message.ReplacementId))
            .ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> RedriveAsync(Guid id, ConflictRedrive input, ClaimsPrincipal actor,
        OutboxData data, CancellationToken ct)
    {
        var replacement = Guid.NewGuid();
        await using var transaction = await data.Database.BeginTransactionAsync(ct);
        // A collision with the same handler type needs investigation. That job might still
        // commit an inbox receipt even if none exists at the moment of this request.
        var changed = await data.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE outbox."Messages" AS message
            SET "ReplacementId" = {replacement}
            WHERE message."Id" = {id} AND message."ConflictAt" IS NOT NULL AND message."ReplacementId" IS NULL
              AND NOT EXISTS (SELECT 1 FROM outbox."InboxReceipts" receipt WHERE receipt."MessageId" = {id})
              AND NOT EXISTS (SELECT 1 FROM jobs."Jobs" job WHERE job."Id" = {id} AND job."Type" = message."Type")
            """, ct);
        if (changed == 0)
            return Results.Problem(statusCode: 409, title: "The outbox conflict is not eligible for redrive.");

        await data.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO outbox."Messages" ("Id", "Type", "Payload", "MaxAttempts", "CreatedAt")
            SELECT {replacement}, "Type", "Payload", "MaxAttempts", now()
            FROM outbox."Messages" WHERE "Id" = {id}
            """, ct);
        await OperationsAudit.RecordAsync(data, actor.UserId(), "outbox.redriven", $"{id}/{replacement}",
            input.Reason, ct);
        await transaction.CommitAsync(ct);
        return Results.Accepted($"/api/v1/admin/outbox?id={replacement}", new { id = replacement });
    }

    private sealed record MessageSummary(Guid Id, string Type, DateTimeOffset CreatedAt,
        DateTimeOffset? DispatchedAt, DateTimeOffset? ConflictAt, Guid? ReplacementId);
    private sealed record ConflictRedrive([property: Required, StringLength(500)] string Reason);
}
