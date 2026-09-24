using System.Security.Claims;
using ICEHOTT.API.Models;
using ICEHOTT.Application.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICEHOTT.API.Controllers;

[ApiController]
[Authorize]
[Route("api/workspaces")]
public sealed class WorkspacesController(WorkspaceService workspaces) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateWorkspaceRequest request, CancellationToken cancellationToken)
    {
        var created = await workspaces.CreateAsync(CurrentUserId(), request.Name, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { workspaceId = created.Id }, created);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await workspaces.ListAsync(CurrentUserId(), cancellationToken));

    [HttpGet("{workspaceId:guid}")]
    public async Task<IActionResult> GetById(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await workspaces.GetAsync(CurrentUserId(), workspaceId, cancellationToken);
        return result.Succeeded ? Ok(result.Workspace) : NotFound(new { code = result.ErrorCode });
    }

    [HttpGet("{workspaceId:guid}/settings")]
    public async Task<IActionResult> GetSettings(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await workspaces.GetSettingsAsync(CurrentUserId(), workspaceId, cancellationToken);
        if (result.Succeeded) return Ok(result.Workspace);
        if (result.ErrorCode == "forbidden") return Forbid();
        return NotFound(new { code = result.ErrorCode });
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
