using System.Security.Claims;
using ICEHOTT.API.Filters;
using ICEHOTT.API.Models;
using ICEHOTT.Application.Workflows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ICEHOTT.API.Controllers;

[ApiController]
[Authorize]
[Route("api/workspaces/{workspaceId:guid}/workflow-runs/{runId:guid}/checkpoints")]
[RequestBodyLimit(4 * 1024)]
public sealed class WorkflowCheckpointsController(
    WorkflowCheckpointService checkpoints) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid workspaceId,
        Guid runId,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await checkpoints.ListAsync(
            CurrentUserId(),
            workspaceId,
            runId,
            limit,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result);
    }

    [HttpGet("{checkpointId:guid}")]
    public async Task<IActionResult> Get(
        Guid workspaceId,
        Guid runId,
        Guid checkpointId,
        CancellationToken cancellationToken = default)
    {
        var result = await checkpoints.GetAsync(
            CurrentUserId(),
            workspaceId,
            runId,
            checkpointId,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result);
    }

    [HttpPost("{checkpointId:guid}/approve")]
    public async Task<IActionResult> Approve(
        Guid workspaceId,
        Guid runId,
        Guid checkpointId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]
        WorkflowCheckpointDecisionRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await checkpoints.ApproveAsync(
            CurrentUserId(),
            workspaceId,
            runId,
            checkpointId,
            request?.Reason,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result);
    }

    [HttpPost("{checkpointId:guid}/reject")]
    public async Task<IActionResult> Reject(
        Guid workspaceId,
        Guid runId,
        Guid checkpointId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]
        WorkflowCheckpointDecisionRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await checkpoints.RejectAsync(
            CurrentUserId(),
            workspaceId,
            runId,
            checkpointId,
            request?.Reason,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result);
    }

    private IActionResult MapError<T>(
        WorkflowCheckpointResult<T> result)
    {
        var payload = new
        {
            code = result.ErrorCode
        };

        return result.ErrorCode switch
        {
            "workspace_not_found" or
            "run_not_found" or
            "checkpoint_not_found" =>
                NotFound(payload),

            "forbidden" or
            "self_decision_forbidden" =>
                StatusCode(
                    StatusCodes.Status403Forbidden,
                    payload),

            "invalid_state" =>
                Conflict(payload),

            "checkpoint_decision_failed" =>
                StatusCode(
                    StatusCodes.Status500InternalServerError,
                    payload),

            _ => BadRequest(payload)
        };
    }

    private Guid CurrentUserId() =>
        Guid.Parse(
            User.FindFirstValue(
                ClaimTypes.NameIdentifier)!);
}
