using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using SuperBackend.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SuperBackend.Jobs;

[ApiController]
[Route("api/v1/admin/jobs")]
public sealed class JobsController(IJobOperations operations) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = JobsModule.Inspect)]
    public async Task<ActionResult<List<JobSummary>>> List([FromQuery] int offset, Guid? id, string? status,
        DateTimeOffset? before, CancellationToken ct)
    {
        if (status is not (null or "pending" or "running" or "succeeded" or "dead"))
            return BadRequest();
        return await operations.ListAsync(offset, id, status, before?.ToUniversalTime(), ct);
    }

    [HttpPost("{id:guid}/retry")]
    [Authorize(Policy = JobsModule.Retry)]
    [ProducesResponseType(204)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> Retry(Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RedriveRequest? input, CancellationToken ct) =>
        await operations.RetryAsync(id, User.UserId(), input?.Reason ?? "Operator retried a dead job.", ct) ? NoContent() : Conflict();

}

public sealed record RedriveRequest([Required, StringLength(500)] string Reason);
