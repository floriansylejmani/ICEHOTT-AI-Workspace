using System.Security.Claims;
using ICEHOTT.API.Models;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Agents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICEHOTT.API.Controllers;

[ApiController]
[Authorize]
[Route("api/workspaces/{workspaceId:guid}/conversations")]
public sealed class AgentController(AgentService agent) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await agent.ListAsync(CurrentUserId(), workspaceId, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : NotFound(new { code = result.ErrorCode });
    }

    [HttpGet("{conversationId:guid}")]
    public async Task<IActionResult> Get(
        Guid workspaceId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var result = await agent.GetAsync(CurrentUserId(), workspaceId, conversationId, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : NotFound(new { code = result.ErrorCode });
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat(
        Guid workspaceId,
        SendAgentMessageRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await agent.SendAsync(
                CurrentUserId(),
                workspaceId,
                request.ConversationId,
                request.Content ?? string.Empty,
                cancellationToken);

            if (result.Succeeded) return Ok(result.Value);
            if (result.ErrorCode is "message_required" or "message_too_long")
                return BadRequest(new { code = result.ErrorCode });
            return NotFound(new { code = result.ErrorCode });
        }
        catch (AiRuntimeUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = "ai_runtime_unavailable" });
        }
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
