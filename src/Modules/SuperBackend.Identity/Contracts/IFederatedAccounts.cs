using System.Security.Claims;

namespace SuperBackend.Identity;

public interface IFederatedAccounts
{
    Task<string?> FindUserAsync(string key);
    Task<ClaimsPrincipal?> CreatePrincipalAsync(string key, string userId);
    Task<FederatedLinkResult> SetLinkAsync(string key, string userId, ClaimsPrincipal actor, bool add, CancellationToken ct);
}

public enum FederatedLinkResult { Success, NotFound, Protected, Conflict }
