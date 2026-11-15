using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SuperBackend.Platform;

namespace SuperBackend.Identity;

internal static class AdminUserEndpoints
{
    public static void Map(RouteGroupBuilder admin)
    {
        admin.MapGet("/users", ListAsync);
        admin.MapPut("/users/{userId}/status", SetStatusAsync).ValidateBody<AccountStatus>();
        admin.MapPost("/users/{userId}/revoke-sessions", RevokeSessionsAsync);
    }

    private static Task<List<AdminUser>> ListAsync(string? search, int? offset, int? limit, IdentityData db,
        ILookupNormalizer normalizer, CancellationToken ct)
    {
        var prefix = normalizer.NormalizeEmail(search?.Trim());
        var administrator = normalizer.NormalizeName(AccessPolicies.Administrator);
        return db.Users.Where(user => string.IsNullOrEmpty(prefix) ||
                (user.NormalizedEmail != null && user.NormalizedEmail.StartsWith(prefix)))
            .OrderBy(user => user.NormalizedEmail).ThenBy(user => user.Id)
            .Skip(AdminEndpoints.Offset(offset)).Take(AdminEndpoints.Limit(limit))
            .Select(user => new AdminUser(user.Id, user.Email, user.EmailConfirmed, user.DisplayName, user.IsDisabled,
                db.UserRoles.Any(membership => membership.UserId == user.Id &&
                    db.Roles.Any(role => role.Id == membership.RoleId && role.NormalizedName == administrator))))
            .ToListAsync(ct);
    }

    private static Task<IResult> SetStatusAsync(string userId, AccountStatus input, ClaimsPrincipal actor, IdentityData db,
        UserManager<Account> users, TimeProvider time, IStringLocalizer<BackendText> text, CancellationToken ct) =>
        ChangeAsync(userId, input.Disabled, actor, db, users, time, text, ct);

    private static Task<IResult> RevokeSessionsAsync(string userId, ClaimsPrincipal actor, IdentityData db,
        UserManager<Account> users, TimeProvider time, IStringLocalizer<BackendText> text, CancellationToken ct) =>
        ChangeAsync(userId, null, actor, db, users, time, text, ct);

    private static async Task<IResult> ChangeAsync(string userId, bool? disabled, ClaimsPrincipal actor, IdentityData db,
        UserManager<Account> users, TimeProvider time, IStringLocalizer<BackendText> text, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return Results.NotFound();
        if (await users.IsInRoleAsync(user, AccessPolicies.Administrator))
            return Results.Problem(statusCode: 403, title: text["AdministratorProtected"]);
        if (disabled == user.IsDisabled)
            return Results.NoContent();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (disabled is bool value)
            user.IsDisabled = value;

        var result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
            return IdentityResponse.Empty(result);

        var action = disabled switch
        {
            true => "user.disabled",
            false => "user.enabled",
            null => "user.sessions.revoked"
        };
        await AdminEndpoints.AuditAsync(db, time, actor, action, null, ct, userId);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private sealed record AdminUser(string Id, string? Email, bool EmailConfirmed, string? DisplayName, bool IsDisabled, bool IsAdministrator);
    private sealed record AccountStatus([property: Required] bool? Disabled);
}
