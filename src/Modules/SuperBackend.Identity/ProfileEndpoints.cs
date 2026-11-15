using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using SuperBackend.Platform;

namespace SuperBackend.Identity;

internal static class ProfileEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var profile = api.MapGroup("/me").WithTags("Profile").RequireAuthorization();
        profile.MapGet("/", async (ClaimsPrincipal principal, UserManager<Account> users) =>
        {
            var account = await users.GetUserAsync(principal);
            return account is null ? Results.Unauthorized() : Results.Ok(ToProfile(account));
        });
        profile.MapPut("/", async (UpdateProfile input, ClaimsPrincipal principal, UserManager<Account> users) =>
        {
            var account = await users.GetUserAsync(principal);
            if (account is null)
                return Results.Unauthorized();

            account.DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? null : input.DisplayName.Trim();
            var result = await users.UpdateAsync(account);
            return result.Succeeded ? Results.Ok(ToProfile(account)) : IdentityResponse.Empty(result);
        }).ValidateBody<UpdateProfile>();
    }

    private static Profile ToProfile(Account account) =>
        new(account.Id, account.Email, account.EmailConfirmed, account.DisplayName);

    private sealed record Profile(string Id, string? Email, bool EmailConfirmed, string? DisplayName);
}

internal static class IdentityResponse
{
    public static IResult Empty(IdentityResult result) =>
        result.Succeeded ? Results.NoContent() :
        result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure))
            ? Results.Conflict()
            : Results.ValidationProblem(result.Errors.GroupBy(error => error.Code)
                .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray()));
}

internal sealed record UpdateProfile([property: StringLength(100)] string? DisplayName);
