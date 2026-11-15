using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SuperBackend.Jobs;

[ApiController]
[Route("api/v1/admin/jobs")]
public sealed class JobsController(IJobOperations operations) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = JobsModule.Inspect)]
    public Task<List<JobSummary>> List([FromQuery] int offset, CancellationToken ct) =>
        operations.ListAsync(offset, ct);

    [HttpPost("{id:guid}/retry")]
    [Authorize(Policy = JobsModule.Retry)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct) =>
        await operations.RetryAsync(id, ct) ? NoContent() : Conflict();

}
