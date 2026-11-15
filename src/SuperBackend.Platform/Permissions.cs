using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace SuperBackend.Platform;

public sealed record Permission(string Name, string Description, bool RequiresMfa = false) : IAuthorizationRequirement;

public static class Permissions
{
    public static AuthorizationBuilder AddPermission(this AuthorizationBuilder builder,
        string name, string description, bool requiresMfa = false)
    {
        var permission = new Permission(name, description, requiresMfa);
        builder.Services.AddSingleton(permission);
        return builder.AddPolicy(name, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(permission));
    }

    public static string UserId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("The authenticated principal has no user identifier.");
}
