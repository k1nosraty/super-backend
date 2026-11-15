using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SuperBackend.Platform;

namespace SuperBackend.Tenancy;

[ApiController]
[Route("api/v1/tenants")]
[Authorize]
public sealed class TenantDirectoryController(ITenantDirectory directory) : ControllerBase
{
    [HttpGet]
    public Task<List<TenantSummary>> List([FromQuery] int offset, CancellationToken ct) =>
        directory.ListAsync(User.UserId(), offset, ct);

    [HttpPost]
    [Authorize(Policy = TenancyModule.Manage)]
    public async Task<ActionResult<TenantSummary>> Create(NewTenant input, CancellationToken ct) =>
        StatusCode(201, await directory.CreateAsync(input.Name, ct));

    [HttpPut("{tenantId:guid}/members/{userId}")]
    [Authorize(Policy = TenancyModule.Manage)]
    public Task<IActionResult> SetMembership(Guid tenantId, string userId, TenantMembership input, CancellationToken ct) =>
        SetAsync(tenantId, userId, input.Access, ct);

    [HttpDelete("{tenantId:guid}/members/{userId}")]
    [Authorize(Policy = TenancyModule.Manage)]
    public Task<IActionResult> RemoveMembership(Guid tenantId, string userId, CancellationToken ct) =>
        SetAsync(tenantId, userId, null, ct);

    private async Task<IActionResult> SetAsync(Guid tenantId, string userId, TenantAccess? access, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 128)
            return BadRequest();
        return await directory.SetMembershipAsync(tenantId, userId, access, ct) ? NoContent() : NotFound();
    }

}
