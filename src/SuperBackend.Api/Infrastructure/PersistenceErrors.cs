using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Localization;
using SuperBackend.Platform;

namespace SuperBackend.Api;

internal sealed class PersistenceErrors : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var databaseError = exception is DbUpdateException update ? update.InnerException : exception;
        if (exception is not DbUpdateConcurrencyException &&
            databaseError is not PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation
            })
            return false;

        await Results.Problem(statusCode: StatusCodes.Status409Conflict, title: context.RequestServices.GetRequiredService<IStringLocalizer<BackendText>>()["DataChanged"])
            .ExecuteAsync(context);
        return true;
    }
}
