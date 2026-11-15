using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace SuperBackend.Identity;

internal sealed class FederatedAccounts(UserManager<Account> users, SignInManager<Account> signIn,
    IdentityData data, TimeProvider time) : IFederatedAccounts
{
    private const string Provider = "OrganizationOidc";

    public async Task<string?> FindUserAsync(string key) => (await FindEligibleAsync(key))?.Id;

    public async Task<ClaimsPrincipal?> CreatePrincipalAsync(string key, string userId)
    {
        var user = await FindEligibleAsync(key);
        return user?.Id == userId ? await signIn.CreateUserPrincipalAsync(user) : null;
    }

    public async Task<FederatedLinkResult> SetLinkAsync(string key, string userId, ClaimsPrincipal actor,
        bool add, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return FederatedLinkResult.NotFound;
        if (await users.IsInRoleAsync(user, AccessPolicies.Administrator) || (add && !await signIn.CanSignInAsync(user)))
            return FederatedLinkResult.Protected;

        await using var transaction = await data.Database.BeginTransactionAsync(ct);
        var existing = await users.FindByLoginAsync(Provider, key);
        if (existing is not null && existing.Id != userId)
            return FederatedLinkResult.Conflict;
        if (add == (existing is not null))
            return FederatedLinkResult.Success;

        var result = add
            ? await users.AddLoginAsync(user, new UserLoginInfo(Provider, key, "Organization OIDC"))
            : await users.RemoveLoginAsync(user, Provider, key);
        if (!result.Succeeded)
            return FederatedLinkResult.Conflict;

        await AdminEndpoints.AuditAsync(data, time, actor, add ? "user.oidc.linked" : "user.oidc.unlinked", null, ct, userId);
        await transaction.CommitAsync(ct);
        return FederatedLinkResult.Success;
    }

    private async Task<Account?> FindEligibleAsync(string key)
    {
        var user = await users.FindByLoginAsync(Provider, key);
        if (user is null || !await signIn.CanSignInAsync(user) || await users.IsLockedOutAsync(user) ||
            await users.IsInRoleAsync(user, AccessPolicies.Administrator))
            return null;

        return user;
    }
}
