using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SuperBackend.Platform;

namespace SuperBackend.Tenancy;

[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/documents")]
[Authorize]
public sealed class TenantDocumentsController(ITenantAccess access, ITenantDocuments documents) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid tenantId, [FromQuery] int offset, CancellationToken ct) =>
        await CanAccessAsync(tenantId, false, ct) ? Ok(await documents.ListAsync(tenantId, offset, ct)) : NotFound();

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid tenantId, Guid id, CancellationToken ct)
    {
        if (!await CanAccessAsync(tenantId, false, ct))
            return NotFound();
        var result = await documents.FindAsync(tenantId, id, ct);
        if (result is null)
            return NotFound();
        Response.Headers.ETag = $"\"{result.Version}\"";
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid tenantId, DocumentInput input, CancellationToken ct)
    {
        if (!await CanAccessAsync(tenantId, true, ct))
            return NotFound();
        var item = await documents.CreateAsync(tenantId, input, ct);
        return CreatedAtAction(nameof(Get), new { tenantId, id = item.Id }, item);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid tenantId, Guid id, DocumentInput input, CancellationToken ct)
    {
        if (!await CanAccessAsync(tenantId, true, ct))
            return NotFound();
        var header = Request.Headers.IfMatch;
        if (header.Count == 0)
            return Problem(statusCode: 428, title: "A document ETag is required.");
        if (header.Count != 1 || header[0] is not { Length: 38 } etag ||
            etag[0] != '"' || etag[^1] != '"' || !Guid.TryParseExact(etag[1..^1], "D", out var version))
            return BadRequest();

        return await documents.UpdateAsync(tenantId, id, version, input, ct) switch
        {
            DocumentUpdate.Updated => NoContent(),
            DocumentUpdate.Stale => Problem(statusCode: 412, title: "The document version changed."),
            _ => NotFound()
        };
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid tenantId, Guid id, CancellationToken ct) =>
        await CanAccessAsync(tenantId, true, ct) && await documents.DeleteAsync(tenantId, id, ct)
            ? NoContent() : NotFound();

    private Task<bool> CanAccessAsync(Guid tenantId, bool write, CancellationToken ct) =>
        access.AllowsAsync(tenantId, User.UserId(), write, ct);
}
