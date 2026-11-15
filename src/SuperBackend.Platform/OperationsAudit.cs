using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace SuperBackend.Platform;

public static class OperationsAudit
{
    public static Task RecordAsync(DbContext data, string actor, string action, string target, string reason,
        CancellationToken ct)
    {
        if (data.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An audit entry must share the operation transaction.");

        var trace = Activity.Current?.TraceId.ToString();
        return data.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity."AccessAudits" ("ActorId", "Action", "UserId", "Reason", "TraceId", "OccurredAt")
            VALUES ({actor}, {action}, {target}, {reason}, {trace}, now())
            """, ct);
    }
}

public interface IAccountDirectory
{
    Task<bool> ExistsAsync(string id, CancellationToken ct);
}
