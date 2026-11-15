using Microsoft.EntityFrameworkCore;

namespace SuperBackend.Identity;

public interface IFederationHandoffs
{
    Task<Guid> IssueAsync(string userId, string externalKey, CancellationToken ct);
    Task<bool> ConsumeAsync(Guid id, string userId, string externalKey, CancellationToken ct);
}

internal sealed record FederationHandoff(Guid Id, string UserId, string ExternalKey, DateTimeOffset ExpiresAt);

internal sealed class FederationHandoffs(IdentityData data, TimeProvider time) : IFederationHandoffs
{
    public async Task<Guid> IssueAsync(string userId, string externalKey, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        data.FederationHandoffs.Add(new FederationHandoff(id, userId, externalKey, time.GetUtcNow().AddMinutes(2)));
        await data.SaveChangesAsync(ct);
        return id;
    }

    public async Task<bool> ConsumeAsync(Guid id, string userId, string externalKey, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await data.FederationHandoffs.Where(ticket => ticket.Id == id &&
            ticket.UserId == userId && ticket.ExternalKey == externalKey && ticket.ExpiresAt > now)
            .ExecuteDeleteAsync(ct) == 1;
    }
}
