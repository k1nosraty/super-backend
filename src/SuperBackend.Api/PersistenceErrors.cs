using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SuperBackend.Api;

internal sealed class PersistenceErrors : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not DbUpdateConcurrencyException &&
            exception is not DbUpdateException
            {
                InnerException: PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation
                }
            })
            return false;

        await Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The data changed. Retry the request.")
            .ExecuteAsync(context);
        return true;
    }
}
