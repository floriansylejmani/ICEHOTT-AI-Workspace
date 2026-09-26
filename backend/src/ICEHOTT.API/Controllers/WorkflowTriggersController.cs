using System.Security.Claims;
using ICEHOTT.API.Filters;
using ICEHOTT.API.Models;
using ICEHOTT.Application.Workflows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICEHOTT.API.Controllers;

[ApiController]
[Authorize]
[Route("api/workspaces/{workspaceId:guid}/workflows/{workflowId:guid}/triggers")]
[RequestBodyLimit(4 * 1024)]
public sealed class WorkflowTriggersController(
    WorkflowTriggerService triggers) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(
        Guid workspaceId,
        Guid workflowId,
        CreateWorkflowTriggerRequest request,
        CancellationToken cancellationToken)
    {
        var result = await triggers.CreateAsync(
            CurrentUserId(),
            workspaceId,
            workflowId,
            request.WorkflowVersionId,
            request.ScheduleExpression,
            request.TimeZoneId,
            cancellationToken);

        return result.Succeeded
            ? Created(
                $"/api/workspaces/{workspaceId}/workflows/{workflowId}/triggers/{result.Value!.Id}",
                result.Value)
            : MapError(result.ErrorCode);
    }

    [HttpGet]
    public async Task<IActionResult> List(
        Guid workspaceId,
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var result = await triggers.ListAsync(
            CurrentUserId(),
            workspaceId,
            workflowId,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result.ErrorCode);
    }

    [HttpPost("{triggerId:guid}/enable")]
    public async Task<IActionResult> Enable(
        Guid workspaceId,
        Guid workflowId,
        Guid triggerId,
        CancellationToken cancellationToken)
    {
        var result = await triggers.EnableAsync(
            CurrentUserId(),
            workspaceId,
            workflowId,
            triggerId,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result.ErrorCode);
    }

    [HttpPost("{triggerId:guid}/disable")]
    public async Task<IActionResult> Disable(
        Guid workspaceId,
        Guid workflowId,
        Guid triggerId,
        CancellationToken cancellationToken)
    {
        var result = await triggers.DisableAsync(
            CurrentUserId(),
            workspaceId,
            workflowId,
            triggerId,
            cancellationToken);

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
            "workflow_version_not_found" or
            "trigger_not_found" =>
                NotFound(payload),

            "forbidden" or
            "run_as_not_authorized" =>
                StatusCode(
                    StatusCodes.Status403Forbidden,
                    payload),

            "trigger_quota_exceeded" =>
                StatusCode(
                    StatusCodes.Status429TooManyRequests,
                    payload),

            "invalid_state" =>
                Conflict(payload),

            "trigger_save_failed" =>
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
