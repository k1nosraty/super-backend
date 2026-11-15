using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SuperBackend.Identity;

internal sealed class AccountSignInManager(UserManager<Account> users, IHttpContextAccessor context,
    IUserClaimsPrincipalFactory<Account> principals, IOptions<IdentityOptions> options,
    ILogger<SignInManager<Account>> logger, IAuthenticationSchemeProvider schemes,
    IUserConfirmation<Account> confirmation)
    : SignInManager<Account>(users, context, principals, options, logger, schemes, confirmation)
{
    public override Task<bool> CanSignInAsync(Account user) =>
        user.IsDisabled ? Task.FromResult(false) : base.CanSignInAsync(user);

    public override async Task<Account?> ValidateSecurityStampAsync(ClaimsPrincipal? principal)
    {
        var account = await base.ValidateSecurityStampAsync(principal);
        return account is { IsDisabled: false } ? account : null;
    }
}
