using Microsoft.EntityFrameworkCore;
using SuperBackend.Platform;

namespace SuperBackend.Identity;

internal sealed class IdentityAccountDirectory(IdentityData data) : IAccountDirectory
{
    public Task<bool> ExistsAsync(string id, CancellationToken ct) =>
        data.Users.AnyAsync(user => user.Id == id, ct);
}
