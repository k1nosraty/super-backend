using System.Diagnostics;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SuperBackend.Platform;

namespace SuperBackend.Identity;

internal static class AdminEndpoints
{
    public static RouteGroupBuilder Map(RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("Access administration")
            .RequireAuthorization(AccessPolicies.AdministrationPolicy);

        admin.MapGet("/permissions", (IEnumerable<Permission> catalog) => catalog.OrderBy(item => item.Name));
        admin.MapGet("/roles", (IdentityData db, int? offset, int? limit, CancellationToken ct) =>
            db.Roles.OrderBy(role => role.Name).ThenBy(role => role.Id)
                .Skip(Offset(offset)).Take(Limit(limit)).Select(role => new { role.Id, role.Name }).ToListAsync(ct));
        AdminUserEndpoints.Map(admin);
        admin.MapGet("/audit", (IdentityData db, long? before, int? limit, CancellationToken ct) =>
            db.AccessAudits.Where(entry => before == null || entry.Id < before)
                .OrderByDescending(entry => entry.Id).Take(Limit(limit)).AsNoTracking().ToListAsync(ct));

        admin.MapPost("/roles", CreateRoleAsync).ValidateBody<CreateRole>();
        admin.MapPut("/roles/{roleId}", RenameRoleAsync).ValidateBody<CreateRole>();
        admin.MapDelete("/roles/{roleId}", DeleteRoleAsync);

        admin.MapGet("/roles/{roleId}/permissions", async (string roleId, IdentityData db, CancellationToken ct) =>
        {
            if (!await db.Roles.AnyAsync(role => role.Id == roleId, ct))
                return Results.NotFound();

            return Results.Ok(await db.RolePermissions.Where(grant => grant.RoleId == roleId)
                .OrderBy(grant => grant.Name).Select(grant => grant.Name).ToListAsync(ct));
        });

        admin.MapGet("/users/{userId}/roles", async (string userId, IdentityData db, CancellationToken ct) =>
        {
            if (!await db.Users.AnyAsync(user => user.Id == userId, ct))
                return Results.NotFound();

            return Results.Ok(await db.UserRoles.Where(membership => membership.UserId == userId)
                .Join(db.Roles, membership => membership.RoleId, role => role.Id, (_, role) => new { role.Id, role.Name })
                .OrderBy(role => role.Name).ToListAsync(ct));
        });

        admin.MapPut("/roles/{roleId}/permissions/{permission}", GrantPermissionAsync);
        admin.MapDelete("/roles/{roleId}/permissions/{permission}", RevokePermissionAsync);
        admin.MapPut("/users/{userId}/roles/{roleId}", AddUserRoleAsync);
        admin.MapDelete("/users/{userId}/roles/{roleId}", RemoveUserRoleAsync);
        return admin;
    }

    private static async Task<Results<Created<RoleView>, ProblemHttpResult, ValidationProblem, Conflict>>
        CreateRoleAsync(CreateRole input, ClaimsPrincipal actor, IdentityData db, RoleManager<IdentityRole> roles,
            TimeProvider time, CancellationToken ct)
    {
        var name = input.Name.Trim();
        if (name.Length == 0)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A name is required."] });

        if (roles.NormalizeKey(name) == roles.NormalizeKey(AccessPolicies.Administrator))
            return TypedResults.Problem(statusCode: 403, title: "This role is reserved.");

        var role = new IdentityRole(name);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await roles.CreateAsync(role);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.DuplicateRoleName)))
                return TypedResults.Conflict();
            return TypedResults.ValidationProblem(result.Errors.GroupBy(error => error.Code)
                .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray()));
        }

        await AuditAsync(db, time, actor, "role.created", role.Id, ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Created((string?)null, new RoleView(role.Id, name));
    }

    private static async Task<IResult> RenameRoleAsync(string roleId, CreateRole input, ClaimsPrincipal actor,
        IdentityData db, RoleManager<IdentityRole> roles, TimeProvider time, CancellationToken ct)
    {
        var role = await roles.FindByIdAsync(roleId);
        if (role is null)
            return Results.NotFound();

        var name = input.Name.Trim();
        if (IsReservedRole(role, roles) || roles.NormalizeKey(name) == roles.NormalizeKey(AccessPolicies.Administrator))
            return Results.Problem(statusCode: 403, title: "This role is reserved.");
        if (role.Name == name)
            return Results.NoContent();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await roles.SetRoleNameAsync(role, name);
        if (result.Succeeded)
            result = await roles.UpdateAsync(role);
        if (!result.Succeeded)
            return result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.DuplicateRoleName))
                ? Results.Conflict()
                : IdentityResponse.Empty(result);

        await AuditAsync(db, time, actor, "role.renamed", role.Id, ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteRoleAsync(string roleId, ClaimsPrincipal actor, IdentityData db,
        RoleManager<IdentityRole> roles, TimeProvider time, CancellationToken ct)
    {
        var role = await roles.FindByIdAsync(roleId);
        if (role is null)
            return Results.NotFound();
        if (IsReservedRole(role, roles))
            return Results.Problem(statusCode: 403, title: "This role is reserved.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await roles.DeleteAsync(role);
        if (!result.Succeeded)
            return IdentityResponse.Empty(result);

        await AuditAsync(db, time, actor, "role.deleted", role.Id, ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static Task<IResult> GrantPermissionAsync(string roleId, string permission, ClaimsPrincipal actor,
        IdentityData db, RoleManager<IdentityRole> roles, IEnumerable<Permission> catalog, TimeProvider time, CancellationToken ct) =>
        SetRolePermissionAsync(roleId, permission, true, actor, db, roles, catalog, time, ct);

    private static Task<IResult> RevokePermissionAsync(string roleId, string permission, ClaimsPrincipal actor,
        IdentityData db, RoleManager<IdentityRole> roles, IEnumerable<Permission> catalog, TimeProvider time, CancellationToken ct) =>
        SetRolePermissionAsync(roleId, permission, false, actor, db, roles, catalog, time, ct);

    private static async Task<IResult> SetRolePermissionAsync(string roleId, string permission, bool grant,
        ClaimsPrincipal actor, IdentityData db, RoleManager<IdentityRole> roles, IEnumerable<Permission> catalog,
        TimeProvider time, CancellationToken ct)
    {
        var role = await roles.FindByIdAsync(roleId);
        if (role is null)
            return Results.NotFound();
        if (IsReservedRole(role, roles))
            return Results.Problem(statusCode: 403, title: "This role is reserved.");
        if (grant && !catalog.Any(entry => entry.Name == permission))
            return Results.NotFound();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changes = grant
            ? await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO identity."RolePermissions" ("RoleId", "Name")
                VALUES ({roleId}, {permission})
                ON CONFLICT DO NOTHING
                """, ct)
            : await db.RolePermissions.Where(entry => entry.RoleId == roleId && entry.Name == permission)
                .ExecuteDeleteAsync(ct);
        if (changes != 0)
            await AuditAsync(db, time, actor, grant ? "permission.granted" : "permission.revoked", roleId, ct,
                permission: permission);

        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static Task<IResult> AddUserRoleAsync(string userId, string roleId, ClaimsPrincipal actor,
        IdentityData db, RoleManager<IdentityRole> roles, TimeProvider time, CancellationToken ct) =>
        SetUserRoleAsync(userId, roleId, true, actor, db, roles, time, ct);

    private static Task<IResult> RemoveUserRoleAsync(string userId, string roleId, ClaimsPrincipal actor,
        IdentityData db, RoleManager<IdentityRole> roles, TimeProvider time, CancellationToken ct) =>
        SetUserRoleAsync(userId, roleId, false, actor, db, roles, time, ct);

    private static async Task<IResult> SetUserRoleAsync(string userId, string roleId, bool assign,
        ClaimsPrincipal actor, IdentityData db, RoleManager<IdentityRole> roles,
        TimeProvider time, CancellationToken ct)
    {
        var role = await roles.FindByIdAsync(roleId);
        if (role is null || !await db.Users.AnyAsync(user => user.Id == userId, ct))
            return Results.NotFound();
        if (IsReservedRole(role, roles))
            return Results.Problem(statusCode: 403, title: "This role is reserved.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changes = assign
            ? await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO identity."AspNetUserRoles" ("UserId", "RoleId")
                VALUES ({userId}, {roleId})
                ON CONFLICT DO NOTHING
                """, ct)
            : await db.UserRoles.Where(membership => membership.UserId == userId && membership.RoleId == roleId)
                .ExecuteDeleteAsync(ct);
        if (changes != 0)
            await AuditAsync(db, time, actor, assign ? "role.assigned" : "role.removed", roleId, ct, userId);

        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static bool IsReservedRole(IdentityRole role, RoleManager<IdentityRole> roles) =>
        role.NormalizedName == roles.NormalizeKey(AccessPolicies.Administrator);

    internal static async Task AuditAsync(IdentityData db, TimeProvider time, ClaimsPrincipal actor,
        string action, string? roleId, CancellationToken ct, string? userId = null, string? permission = null)
    {
        db.AccessAudits.Add(new AccessAudit
        {
            OccurredAt = time.GetUtcNow(),
            ActorId = actor.UserId(),
            Action = action,
            RoleId = roleId,
            UserId = userId,
            Permission = permission,
            Reason = action,
            TraceId = Activity.Current?.TraceId.ToString()
        });
        await db.SaveChangesAsync(ct);
    }

    internal static int Offset(int? value) => Math.Max(value ?? 0, 0);
    internal static int Limit(int? value) => Math.Clamp(value ?? 25, 1, 100);

    private sealed record RoleView(string Id, string Name);
}

internal sealed record CreateRole([property: Required, StringLength(100)] string Name);
