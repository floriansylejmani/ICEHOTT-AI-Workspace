using System.Security.Claims;
using ICEHOTT.API.Models;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Knowledge;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICEHOTT.API.Controllers;

[ApiController]
[Authorize]
[Route("api/workspaces/{workspaceId:guid}/knowledge")]
public sealed class KnowledgeController(KnowledgeService knowledge) : ControllerBase
{
    [HttpGet("documents")]
    public async Task<IActionResult> ListDocuments(
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var result = await knowledge.ListAsync(
            CurrentUserId(),
            workspaceId,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : NotFound(new { code = result.ErrorCode });
    }

    [HttpPost("documents")]
    public async Task<IActionResult> Ingest(
        Guid workspaceId,
        IngestKnowledgeRequest request,
        CancellationToken cancellationToken)
    {
        var result = await knowledge.IngestAsync(
            CurrentUserId(),
            workspaceId,
            request.Title ?? string.Empty,
            request.SourceName,
            request.Content ?? string.Empty,
            cancellationToken);

        return ToIngestResult(result);
    }

    [HttpPost("documents/upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(KnowledgeService.MaxUploadBytes + 1_048_576)]
    public async Task<IActionResult> Upload(
        Guid workspaceId,
        [FromForm] UploadKnowledgeDocumentRequest request,
        CancellationToken cancellationToken)
    {
        if (request.File is null)
            return BadRequest(new { code = "file_required" });

        var safeFileName = Path.GetFileName(request.File.FileName);
        await using var stream = request.File.OpenReadStream();

        var result = await knowledge.IngestFileAsync(
            CurrentUserId(),
            workspaceId,
            request.Title,
            safeFileName,
            request.File.ContentType,
            request.File.Length,
            stream,
            cancellationToken);

        return ToIngestResult(result);
    }

    [HttpPost("documents/{documentId:guid}/reindex")]
    public async Task<IActionResult> Reindex(
        Guid workspaceId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var result = await knowledge.ReindexAsync(
            CurrentUserId(),
            workspaceId,
            documentId,
            cancellationToken);

        if (result.Succeeded)
            return StatusCode(StatusCodes.Status202Accepted, result.Value);

        return NotFound(new { code = result.ErrorCode });
    }

    [HttpDelete("documents/{documentId:guid}")]
    public async Task<IActionResult> Delete(
        Guid workspaceId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var result = await knowledge.DeleteAsync(
            CurrentUserId(),
            workspaceId,
            documentId,
            cancellationToken);

        if (result.Succeeded) return NoContent();
        return NotFound(new { code = result.ErrorCode });
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(
        Guid workspaceId,
        [FromQuery] string query,
        [FromQuery] int limit = 5,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await knowledge.SearchAsync(
                CurrentUserId(),
                workspaceId,
                query ?? string.Empty,
                limit,
                cancellationToken);

            if (result.Succeeded) return Ok(result.Value);
            if (result.ErrorCode == "workspace_not_found")
                return NotFound(new { code = result.ErrorCode });
            return BadRequest(new { code = result.ErrorCode });
        }
        catch (AiRuntimeUnavailableException)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { code = "embedding_runtime_unavailable" });
        }
        catch (VectorStoreUnavailableException)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { code = "vector_store_unavailable" });
        }
    }

    private IActionResult ToIngestResult(
        KnowledgeResult<KnowledgeDocumentView> result)
    {
        if (result.Succeeded)
            return StatusCode(StatusCodes.Status202Accepted, result.Value);

        if (result.ErrorCode == "workspace_not_found")
            return NotFound(new { code = result.ErrorCode });

        if (result.ErrorCode == "file_too_large")
            return StatusCode(
                StatusCodes.Status413PayloadTooLarge,
                new { code = result.ErrorCode });

        if (result.ErrorCode == "unsupported_file_type")
            return StatusCode(
                StatusCodes.Status415UnsupportedMediaType,
                new { code = result.ErrorCode });

        return BadRequest(new { code = result.ErrorCode });
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
