using System.Security.Claims;
using ICEHOTT.Application.Workflows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICEHOTT.API.Controllers;

[ApiController]
[Authorize]
[Route("api/workspaces/{workspaceId:guid}/workflows")]
public sealed class WorkflowsController(
    WorkflowExperienceService experience) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid workspaceId,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await experience.ListDefinitionsAsync(
            CurrentUserId(),
            workspaceId,
            limit,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result.ErrorCode);
    }

    [HttpGet("{workflowId:guid}")]
    public async Task<IActionResult> Get(
        Guid workspaceId,
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var result = await experience.GetDefinitionAsync(
            CurrentUserId(),
            workspaceId,
            workflowId,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result.ErrorCode);
    }

    private IActionResult MapError(string? code) =>
        code is "workspace_not_found" or "workflow_not_found"
            ? NotFound(new { code })
            : BadRequest(new { code });

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

[ApiController]
[Authorize]
[Route("api/workspaces/{workspaceId:guid}/workflow-runs")]
public sealed class WorkflowRunsController(
    WorkflowExperienceService experience) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid workspaceId,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var result = await experience.ListRunsAsync(
            CurrentUserId(),
            workspaceId,
            limit,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result.ErrorCode);
    }

    [HttpGet("{runId:guid}")]
    public async Task<IActionResult> Get(
        Guid workspaceId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var result = await experience.GetRunAsync(
            CurrentUserId(),
            workspaceId,
            runId,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result.ErrorCode);
    }

    private IActionResult MapError(string? code) =>
        code is "workspace_not_found" or "run_not_found"
            ? NotFound(new { code })
            : BadRequest(new { code });

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
