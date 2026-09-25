using System.Security.Claims;
using ICEHOTT.API.Models;
using ICEHOTT.Application.Tools;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICEHOTT.API.Controllers;

[ApiController]
[Authorize]
[Route("api/workspaces/{workspaceId:guid}")]
public sealed class ToolsController(
    ToolExecutionService tools) : ControllerBase
{
    [HttpGet("tools")]
    public async Task<IActionResult> ListTools(
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var result = await tools.ListToolsAsync(
            CurrentUserId(),
            workspaceId,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result);
    }

    [HttpPost("tool-executions")]
    public async Task<IActionResult> RequestExecution(
        Guid workspaceId,
        CreateToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await tools.RequestAsync(
            CurrentUserId(),
            workspaceId,
            request.ToolName ?? string.Empty,
            request.Arguments,
            request.IdempotencyKey ?? string.Empty,
            cancellationToken);

        if (result.Succeeded)
            return Ok(result.Value);

        return MapError(result);
    }

    [HttpGet("tool-executions")]
    public async Task<IActionResult> ListExecutions(
        Guid workspaceId,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await tools.ListExecutionsAsync(
            CurrentUserId(),
            workspaceId,
            limit,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result);
    }

    [HttpGet("tool-executions/{executionId:guid}")]
    public async Task<IActionResult> GetExecution(
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        var result = await tools.GetAsync(
            CurrentUserId(),
            workspaceId,
            executionId,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result);
    }

    [HttpPost("tool-executions/{executionId:guid}/approve")]
    public async Task<IActionResult> Approve(
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        var result = await tools.ApproveAsync(
            CurrentUserId(),
            workspaceId,
            executionId,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result);
    }

    [HttpPost("tool-executions/{executionId:guid}/reject")]
    public async Task<IActionResult> Reject(
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        var result = await tools.RejectAsync(
            CurrentUserId(),
            workspaceId,
            executionId,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result);
    }

    [HttpPost("tool-executions/{executionId:guid}/cancel")]
    public async Task<IActionResult> Cancel(
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        var result = await tools.CancelAsync(
            CurrentUserId(),
            workspaceId,
            executionId,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result);
    }

    private IActionResult MapError<T>(
        ToolOperationResult<T> result)
    {
        var payload = new
        {
            code = result.ErrorCode,
            errors = result.ValidationErrors
        };

        return result.ErrorCode switch
        {
            "workspace_not_found" or
            "tool_not_found" or
            "execution_not_found" =>
                NotFound(payload),

            "forbidden" or
            "self_approval_forbidden" =>
                StatusCode(
                    StatusCodes.Status403Forbidden,
                    payload),

            "idempotency_conflict" or
            "invalid_state" or
            "execution_running" or
            "requester_no_longer_authorized" or
            "tool_disabled" or
            "policy_changed" or
            "policy_limit_exceeded" =>
                Conflict(payload),

            "tool_timeout" =>
                StatusCode(
                    StatusCodes.Status504GatewayTimeout,
                    new
                    {
                        code = result.ErrorCode,
                        execution = result.Value
                    }),

            "tool_execution_failed" =>
                StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new
                    {
                        code = result.ErrorCode,
                        execution = result.Value
                    }),

            _ => BadRequest(payload)
        };
    }

    private Guid CurrentUserId() =>
        Guid.Parse(
            User.FindFirstValue(
                ClaimTypes.NameIdentifier)!);
}
