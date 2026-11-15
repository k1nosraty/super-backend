using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace SuperBackend.Platform;

public static class RequestValidation
{
    public static RouteHandlerBuilder ValidateBody<T>(this RouteHandlerBuilder builder) where T : class =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var body = context.Arguments.OfType<T>().Single();
            var errors = new List<ValidationResult>();
            if (Validator.TryValidateObject(body, new ValidationContext(body), errors, validateAllProperties: true))
                return await next(context);

            var details = errors.SelectMany(error =>
                    error.MemberNames.DefaultIfEmpty("").Select(member => (Member: member, Message: error.ErrorMessage!)))
                .GroupBy(error => error.Member, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(error => error.Message).ToArray());
            return Results.ValidationProblem(details);
        }).ProducesValidationProblem();
}
