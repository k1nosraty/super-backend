using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuperBackend.Platform;

namespace SuperBackend.Identity;

public interface ISessionIssuer
{
    Task<ClaimsPrincipal> StartAsync(ClaimsPrincipal principal, CancellationToken ct);
}

internal sealed record AccountSession(Guid Id, string UserId, DateTimeOffset ExpiresAt)
{
    public int Generation { get; init; }
}

internal sealed class AccountSessions(IdentityData data, TimeProvider time) : ISessionIssuer
{
    internal const string SessionClaim = "superbackend.session";
    private const string GenerationClaim = "superbackend.refresh_generation";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public async Task<ClaimsPrincipal> StartAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        data.Sessions.Add(new AccountSession(id, principal.UserId(), time.GetUtcNow() + Lifetime));
        await data.SaveChangesAsync(ct);
        return Attach(principal, id, 0);
    }

    public Task<bool> IsActiveAsync(ClaimsPrincipal principal, CancellationToken ct) =>
        Guid.TryParse(principal.FindFirstValue(SessionClaim), out var id) &&
        principal.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId
            ? Active(id, userId).AnyAsync(ct)
            : Task.FromResult(false);

    public async Task RevokeAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (Guid.TryParse(principal.FindFirstValue(SessionClaim), out var id))
            await Active(id, principal.UserId()).ExecuteDeleteAsync(ct);
    }

    public Task<int> RevokeAllAsync(string userId, CancellationToken ct) =>
        data.Sessions.Where(session => session.UserId == userId).ExecuteDeleteAsync(ct);

    public async Task<ClaimsPrincipal?> RotateAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue(SessionClaim), out var id) ||
            !int.TryParse(principal.FindFirstValue(GenerationClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
            return null;

        var changed = await Active(id, principal.UserId()).Where(session => session.Generation == generation)
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.Generation, session => session.Generation + 1), ct);
        return changed == 1 ? Attach(principal, id, generation + 1) : null;
    }

    private IQueryable<AccountSession> Active(Guid id, string userId)
    {
        var now = time.GetUtcNow();
        return data.Sessions.Where(session => session.Id == id && session.UserId == userId && session.ExpiresAt > now);
    }

    private static ClaimsPrincipal Attach(ClaimsPrincipal principal, Guid id, int generation)
    {
        var clone = new ClaimsPrincipal(principal.Identities.Select(identity => identity.Clone()));
        var identity = (ClaimsIdentity)clone.Identity!;
        foreach (var claim in identity.FindAll(SessionClaim).Concat(identity.FindAll(GenerationClaim)).ToArray())
            identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(SessionClaim, id.ToString()));
        identity.AddClaim(new Claim(GenerationClaim, generation.ToString(CultureInfo.InvariantCulture)));
        return clone;
    }

    public static async Task RefreshAsync(HttpContext context)
    {
        if (!context.Request.HasJsonContentType())
        {
            await Results.Problem(statusCode: 415).ExecuteAsync(context);
            return;
        }

        RefreshRequest? input;
        try
        {
            input = await context.Request.ReadFromJsonAsync<RefreshRequest>(context.RequestAborted);
        }
        catch (System.Text.Json.JsonException)
        {
            await Results.Problem(statusCode: 400).ExecuteAsync(context);
            return;
        }

        if (input?.RefreshToken is not { Length: > 0 and <= 8192 } token)
        {
            await Results.Problem(statusCode: 400).ExecuteAsync(context);
            return;
        }

        var services = context.RequestServices;
        var options = services.GetRequiredService<IOptionsMonitor<BearerTokenOptions>>().Get(IdentityConstants.BearerScheme);
        var ticket = options.RefreshTokenProtector.Unprotect(token);
        var time = services.GetRequiredService<TimeProvider>();
        var signIn = services.GetRequiredService<SignInManager<Account>>();
        if (ticket?.Properties.ExpiresUtc is not { } expiry || expiry <= time.GetUtcNow() ||
            await signIn.ValidateSecurityStampAsync(ticket.Principal) is null ||
            await services.GetRequiredService<AccountSessions>().RotateAsync(ticket.Principal, context.RequestAborted) is not { } principal)
        {
            await Results.Unauthorized().ExecuteAsync(context);
            return;
        }

        await context.SignInAsync(IdentityConstants.BearerScheme, principal);
    }
}
