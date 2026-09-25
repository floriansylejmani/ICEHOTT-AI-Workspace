using System.Security.Claims;
using System.Text.Json;
using ICEHOTT.API.Filters;
using ICEHOTT.Application.Tools;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICEHOTT.API.Controllers;

/// <summary>
/// Phase 4.5 packet B: per-workspace tool policy overlay. Authorization is
/// resolved from database membership inside <see cref="ToolPolicyService"/>
/// (read: Admin/Owner, write and audit: Owner).
/// </summary>
[ApiController]
[Authorize]
[Route("api/workspaces/{workspaceId:guid}/tool-policies")]
[RequestBodyLimit(ToolRequestLimits.MaxPolicyRequestBytes)]
public sealed class ToolPoliciesController(ToolPolicyService policies) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid workspaceId, CancellationToken cancellationToken) =>
        Map(await policies.ListAsync(CurrentUserId(), workspaceId, cancellationToken));

    [HttpGet("{toolName}")]
    public async Task<IActionResult> Get(Guid workspaceId, string toolName, CancellationToken cancellationToken) =>
        Map(await policies.GetAsync(CurrentUserId(), workspaceId, toolName, cancellationToken));

    [HttpGet("{toolName}/audit")]
    public async Task<IActionResult> Audit(Guid workspaceId, string toolName, CancellationToken cancellationToken) =>
        Map(await policies.ListAuditAsync(CurrentUserId(), workspaceId, toolName, cancellationToken));

    [HttpPut("{toolName}")]
    public async Task<IActionResult> Update(
        Guid workspaceId,
        string toolName,
        [FromBody] JsonElement body,
        CancellationToken cancellationToken) =>
        Map(await policies.UpdateAsync(CurrentUserId(), workspaceId, toolName, body, cancellationToken));

    private IActionResult Map<T>(ToolOperationResult<T> result)
    {
        if (result.Succeeded)
            return Ok(result.Value);

        var payload = new { code = result.ErrorCode, errors = result.ValidationErrors };
        return result.ErrorCode switch
        {
            "workspace_not_found" or "tool_not_found" => NotFound(payload),
            "forbidden" => StatusCode(StatusCodes.Status403Forbidden, payload),
            "policy_version_conflict" => Conflict(payload),
            _ => BadRequest(payload)
        };
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
