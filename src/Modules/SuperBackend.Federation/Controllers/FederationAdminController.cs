using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SuperBackend.Identity;

namespace SuperBackend.Federation;

[ApiController]
[Route("api/v1/admin/oidc-links")]
[Authorize(Policy = IdentityModule.AdministrationPolicy)]
public sealed class FederationAdminController(IFederatedAccounts accounts) : ControllerBase
{
    [HttpPut]
    public Task<IActionResult> Link(ExternalAccountLink input, CancellationToken ct) => SetAsync(input, true, ct);

    [HttpPost("remove")]
    public Task<IActionResult> Unlink(ExternalAccountLink input, CancellationToken ct) => SetAsync(input, false, ct);

    private async Task<IActionResult> SetAsync(ExternalAccountLink input, bool add, CancellationToken ct)
    {
        var settings = HttpContext.RequestServices.GetRequiredService<FederationSettings>();
        var key = ExternalIdentity.Key(settings.Issuer, input.Subject);
        return await accounts.SetLinkAsync(key, input.UserId, User, add, ct) switch
        {
            FederatedLinkResult.Success => NoContent(),
            FederatedLinkResult.NotFound => NotFound(),
            FederatedLinkResult.Protected => Forbid(),
            _ => Conflict()
        };
    }
}
