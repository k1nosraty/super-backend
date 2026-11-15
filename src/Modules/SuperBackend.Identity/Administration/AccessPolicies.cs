using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SuperBackend.Platform;

namespace SuperBackend.Identity;

internal sealed class AccessPolicies(IdentityData data, ILookupNormalizer normalizer,
    IHttpContextAccessor accessor, PrivilegedSessionPolicy policy) : AuthorizationHandler<Permission>
{
    public const string Administrator = "Administrator";
    public const string AdministrationPolicy = "identity.administration";

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        Permission requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return;

        var userId = context.User.UserId();
        var cancellation = accessor.HttpContext?.RequestAborted ?? default;
        var administrator = normalizer.NormalizeName(Administrator);
        var privileged = !policy.RequireMfa || context.User.HasClaim("amr", "mfa");
        if (requirement.RequiresMfa && !privileged)
            return;
        var allowDelegation = requirement.Name != AdministrationPolicy;
        var allowed = await data.Roles
            .Where(role => data.UserRoles.Any(membership => membership.RoleId == role.Id && membership.UserId == userId))
            .AnyAsync(role => (privileged && role.NormalizedName == administrator) ||
                (allowDelegation && data.RolePermissions.Any(grant =>
                    grant.RoleId == role.Id && grant.Name == requirement.Name)), cancellation);
        if (allowed)
            context.Succeed(requirement);
    }
}

internal sealed record PrivilegedSessionPolicy(bool RequireMfa);

internal sealed class ValidateSession(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SignInManager<Account> signIn, AccountSessions sessions)
    {
        if (context.User.Identity?.IsAuthenticated == true &&
            (await signIn.ValidateSecurityStampAsync(context.User) is null ||
            !await sessions.IsActiveAsync(context.User, context.RequestAborted)))
        {
            context.User = new(new System.Security.Claims.ClaimsIdentity());
        }

        await next(context);
    }
}
