using System.Security.Claims;
using ICEHOTT.API.Filters;
using ICEHOTT.Application.Workflows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICEHOTT.API.Controllers;

[ApiController]
[Authorize]
[Route("api/workspaces/{workspaceId:guid}/workflow-runs")]
[RequestBodyLimit(4 * 1024)]
public sealed class WorkflowRunMutationsController(
    WorkflowAdministrationService administration) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(
        Guid workspaceId,
        CreateWorkflowRunRequest request,
        CancellationToken cancellationToken)
    {
        var result = await administration.CreateRunAsync(
            CurrentUserId(), workspaceId, request.WorkflowDefinitionId,
            request.IdempotencyKey, cancellationToken);

        if (!result.Succeeded)
            return MapError(result.ErrorCode);

        if (result.IsReplay)
            return Ok(result.Value);

        return Created(
            $"/api/workspaces/{workspaceId}/workflow-runs/{result.Value!.Id}",
            result.Value);
    }

    [HttpPost("{runId:guid}/cancel")]
    public async Task<IActionResult> Cancel(
        Guid workspaceId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var result = await administration.CancelRunAsync(
            CurrentUserId(), workspaceId, runId, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result.ErrorCode);
    }

    [HttpPost("{runId:guid}/retry")]
    public async Task<IActionResult> Retry(
        Guid workspaceId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var result = await administration.RetryRunAsync(
            CurrentUserId(), workspaceId, runId, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result.ErrorCode);
    }

    private IActionResult MapError(string? code)
    {
        var payload = new { code };
        return code switch
        {
            "workspace_not_found" or
            "workflow_not_found" or
            "workflow_run_not_found" => NotFound(payload),

            "forbidden" or
            "workflow_run_not_authorized" =>
                StatusCode(StatusCodes.Status403Forbidden, payload),

            "invalid_state" or
            "concurrency_conflict" or
            "idempotency_conflict" or
            "run_retry_not_supported" => Conflict(payload),

            _ => BadRequest(payload)
        };
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

public sealed record CreateWorkflowRunRequest(
    Guid WorkflowDefinitionId,
    string IdempotencyKey);
