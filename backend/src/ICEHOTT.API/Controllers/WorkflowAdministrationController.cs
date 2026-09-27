using System.Security.Claims;
using System.Text.Json;
using ICEHOTT.API.Filters;
using ICEHOTT.Application.Workflows;
using ICEHOTT.Domain.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICEHOTT.API.Controllers;

[ApiController]
[Authorize]
[Route("api/workspaces/{workspaceId:guid}/workflows")]
[RequestBodyLimit(80 * 1024)]
public sealed class WorkflowAdministrationController(
    WorkflowAdministrationService administration) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(
        Guid workspaceId,
        CreateWorkflowDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await administration.CreateDefinitionAsync(
            CurrentUserId(), workspaceId, request.Name, request.Description,
            request.MinimumRunRole, cancellationToken);

        return result.Succeeded
            ? Created(
                $"/api/workspaces/{workspaceId}/workflows/{result.Value!.Id}",
                result.Value)
            : MapError(result.ErrorCode);
    }

    [HttpPost("{workflowId:guid}/versions")]
    public async Task<IActionResult> CreateVersion(
        Guid workspaceId,
        Guid workflowId,
        CreateWorkflowVersionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Definition.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return BadRequest(new { code = "invalid_workflow_definition" });

        var result = await administration.CreateVersionAsync(
            CurrentUserId(), workspaceId, workflowId,
            request.Definition.GetRawText(), cancellationToken);

        return result.Succeeded
            ? Created(
                $"/api/workspaces/{workspaceId}/workflows/{workflowId}",
                result.Value)
            : MapError(result.ErrorCode);
    }

    [HttpPost("{workflowId:guid}/versions/{versionId:guid}/activate")]
    public async Task<IActionResult> ActivateVersion(
        Guid workspaceId,
        Guid workflowId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        var result = await administration.ActivateVersionAsync(
            CurrentUserId(), workspaceId, workflowId, versionId,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result.ErrorCode);
    }

    [HttpPost("{workflowId:guid}/archive")]
    public async Task<IActionResult> Archive(
        Guid workspaceId,
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        var result = await administration.ArchiveDefinitionAsync(
            CurrentUserId(), workspaceId, workflowId, cancellationToken);

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
            "workflow_version_not_found" => NotFound(payload),

            "forbidden" => StatusCode(StatusCodes.Status403Forbidden, payload),

            "invalid_state" or
            "concurrency_conflict" => Conflict(payload),

            _ => BadRequest(payload)
        };
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

public sealed record CreateWorkflowDefinitionRequest(
    string Name,
    string? Description,
    WorkspaceRole MinimumRunRole = WorkspaceRole.Member);

public sealed record CreateWorkflowVersionRequest(JsonElement Definition);
