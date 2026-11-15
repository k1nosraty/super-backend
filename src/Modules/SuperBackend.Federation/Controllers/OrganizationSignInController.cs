using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using SuperBackend.Identity;

namespace SuperBackend.Federation;

[ApiController]
[Route("api/v1/oidc")]
[AllowAnonymous]
[EnableRateLimiting(IdentityModule.RateLimit)]
public sealed class OrganizationSignInController : ControllerBase
{
    [HttpGet("login")]
    public IActionResult Login() =>
        Challenge(new AuthenticationProperties { RedirectUri = "/api/v1/oidc/session" }, FederationModule.Remote);

    [HttpGet("session")]
    public async Task<IActionResult> Session()
    {
        var ticket = await HttpContext.AuthenticateAsync(FederationModule.Exchange);
        return ticket.Succeeded ? Ok(new { ready = true }) : Unauthorized();
    }

    [HttpPost("exchange")]
    public async Task<IResult> Exchange([FromServices] IFederatedAccounts accounts)
    {
        var settings = HttpContext.RequestServices.GetRequiredService<FederationSettings>();
        if (Request.Headers.Origin.Count != 1 || Request.Headers.Origin[0] != settings.PublicOrigin)
            return Results.Problem(statusCode: 403, title: "A same-origin request is required.");

        var ticket = await HttpContext.AuthenticateAsync(FederationModule.Exchange);
        if (!ticket.Succeeded || ticket.Properties is null ||
            !ticket.Properties.Items.TryGetValue("external-key", out var key) || key is null ||
            !ticket.Properties.Items.TryGetValue("local-user", out var id) || id is null)
            return Results.Unauthorized();

        await HttpContext.SignOutAsync(FederationModule.Exchange);
        var principal = await accounts.CreatePrincipalAsync(key, id);
        return principal is null ? Results.Unauthorized() :
            Results.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
    }
}
