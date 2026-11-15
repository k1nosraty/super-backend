using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Localization;
using SuperBackend.Platform;

namespace SuperBackend.Identity;

internal static class AccountInvitations
{
    private const string Purpose = "AccountInvitation";

    public static void Map(RouteGroupBuilder admin, RouteGroupBuilder auth)
    {
        admin.MapPost("/invitations", InviteAsync).RequireRateLimiting(IdentityModule.RateLimit)
            .ValidateBody<InviteAccount>();
        auth.MapPost("/accept-invitation", AcceptAsync).AllowAnonymous().ValidateBody<AcceptInvitation>();
    }

    private static async Task<IResult> InviteAsync(InviteAccount input, ClaimsPrincipal actor, UserManager<Account> users,
        SmtpEmailSender sender, IdentityData db, TimeProvider time, IStringLocalizer<BackendText> text, CancellationToken ct)
    {
        var email = input.Email.Trim();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var account = await users.FindByEmailAsync(email);
        if (account is null)
        {
            account = new Account { Email = email, UserName = email };
            var result = await users.CreateAsync(account);
            if (!result.Succeeded)
                return IdentityResponse.Empty(result);
            await AdminEndpoints.AuditAsync(db, time, actor, "user.invited", null, ct, account.Id);
        }
        else if (account.EmailConfirmed || account.IsDisabled || await users.HasPasswordAsync(account))
        {
            return Results.Problem(statusCode: 409, title: text["AccountAlreadyExists"]);
        }

        var code = await users.GenerateUserTokenAsync(account, TokenOptions.DefaultProvider, Purpose);
        var encoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
        if (sender.Queued)
            await sender.SendInvitationAsync(email, encoded, transaction.GetDbTransaction(), ct);
        await transaction.CommitAsync(ct);
        if (!sender.Queued)
            await sender.SendInvitationAsync(email, encoded, ct: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AcceptAsync(AcceptInvitation input, UserManager<Account> users,
        IStringLocalizer<BackendText> text)
    {
        var account = await users.FindByEmailAsync(input.Email.Trim());
        if (account is null || account.EmailConfirmed || account.IsDisabled || await users.HasPasswordAsync(account))
            return Invalid(text);

        string code;
        try
        {
            code = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(input.Code));
        }
        catch (FormatException)
        {
            return Invalid(text);
        }

        if (!await users.VerifyUserTokenAsync(account, TokenOptions.DefaultProvider, Purpose, code))
            return Invalid(text);

        account.EmailConfirmed = true;
        return IdentityResponse.Empty(await users.AddPasswordAsync(account, input.Password));
    }

    private static IResult Invalid(IStringLocalizer<BackendText> text) =>
        Results.Problem(statusCode: 400, title: text["InvalidInvitation"]);

    private sealed record InviteAccount(
        [property: Required, EmailAddress, StringLength(256)] string Email);
    private sealed record AcceptInvitation(
        [property: Required, EmailAddress, StringLength(256)] string Email,
        [property: Required, StringLength(4096)] string Code,
        [property: Required, StringLength(256)] string Password);
}
