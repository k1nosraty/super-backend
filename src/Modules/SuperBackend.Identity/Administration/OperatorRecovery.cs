using System.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SuperBackend.Identity;

public static class OperatorRecovery
{
    public static async Task RecoverAdministratorAsync(this IServiceProvider services, string email, string actor,
        string reason, string action, string? replacementPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new ArgumentException("A named operator and a reason of at most 500 characters are required.");
        if (action is not ("revoke" or "suspend" or "reset-mfa"))
            throw new ArgumentException("Choose revoke, suspend or reset-mfa.");
        if (action == "reset-mfa" && string.IsNullOrWhiteSpace(replacementPassword))
            throw new ArgumentException("A replacement password is required to reset MFA.");

        await using var scope = services.CreateAsyncScope();
        var data = scope.ServiceProvider.GetRequiredService<IdentityData>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var policy = scope.ServiceProvider.GetRequiredService<PrivilegedSessionPolicy>();
        await using var transaction = await data.Database.BeginTransactionAsync(ct);
        var role = await roles.FindByNameAsync(AccessPolicies.Administrator)
            ?? throw new InvalidOperationException("No administrator role exists.");
        await data.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM identity."AspNetRoles" WHERE "Id" = {role.Id} FOR UPDATE""", ct);
        var user = await users.FindByEmailAsync(email);
        if (user is null || !await users.IsInRoleAsync(user, AccessPolicies.Administrator))
            throw new InvalidOperationException("The target is not an administrator.");

        if (action == "suspend" && !user.IsDisabled)
        {
            var others = await data.UserRoles.Where(membership => membership.RoleId == role.Id)
                .Join(data.Users, membership => membership.UserId, account => account.Id, (_, account) => account)
                .AnyAsync(account => account.Id != user.Id && !account.IsDisabled &&
                    account.EmailConfirmed && account.PasswordHash != null &&
                    (account.LockoutEnd == null || account.LockoutEnd <= time.GetUtcNow()) &&
                    (!policy.RequireMfa || !user.TwoFactorEnabled || account.TwoFactorEnabled), ct);
            if (!others)
                throw new InvalidOperationException("The last usable administrator cannot be suspended.");
            user.IsDisabled = true;
        }

        if (action == "reset-mfa")
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            Ensure(await users.ResetPasswordAsync(user, token, replacementPassword!));
            Ensure(await users.SetTwoFactorEnabledAsync(user, false));
            Ensure(await users.ResetAuthenticatorKeyAsync(user));
            await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
            Ensure(await users.SetLockoutEndDateAsync(user, null));
            Ensure(await users.ResetAccessFailedCountAsync(user));
            user.IsDisabled = false;
        }

        Ensure(await users.UpdateSecurityStampAsync(user));
        await scope.ServiceProvider.GetRequiredService<AccountSessions>().RevokeAllAsync(user.Id, ct);
        data.AccessAudits.Add(new AccessAudit
        {
            ActorId = actor,
            Action = $"administrator.recovery.{action}",
            UserId = user.Id,
            RoleId = role.Id,
            OccurredAt = time.GetUtcNow(),
            Reason = reason,
            TraceId = Activity.Current?.TraceId.ToString() ?? ActivityTraceId.CreateRandom().ToString()
        });
        await data.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(error => error.Code)));
    }
}
