using System.Security.Claims;
using ICEHOTT.API.Models;
using ICEHOTT.Application.Artifacts;
using ICEHOTT.Domain.Workflows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICEHOTT.API.Controllers;

[ApiController]
[Authorize]
[Route("api/workspaces/{workspaceId:guid}/artifacts")]
public sealed class ArtifactsController(
    ArtifactService artifacts) : ControllerBase
{
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ArtifactPolicy.AbsoluteMaxArtifactBytes + 1_048_576)]
    public async Task<IActionResult> Upload(
        Guid workspaceId,
        [FromForm] UploadArtifactRequest request,
        CancellationToken cancellationToken)
    {
        if (request.File is null)
            return BadRequest(new { code = "file_required" });

        await using var stream = request.File.OpenReadStream();

        var result = await artifacts.UploadAsync(
            CurrentUserId(),
            workspaceId,
            request.File.FileName,
            request.File.ContentType,
            request.File.Length,
            stream,
            request.IdempotencyKey,
            cancellationToken: cancellationToken);

        if (!result.Succeeded)
            return MapError(result);

        if (result.Value!.Status == ArtifactStatus.Pending)
        {
            return AcceptedAtAction(
                nameof(Get),
                new
                {
                    workspaceId,
                    artifactId = result.Value.Id
                },
                result.Value);
        }

        if (result.IsReplay)
            return Ok(result.Value);

        return CreatedAtAction(
            nameof(Get),
            new
            {
                workspaceId,
                artifactId = result.Value.Id
            },
            result.Value);
    }

    [HttpGet]
    public async Task<IActionResult> List(
        Guid workspaceId,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await artifacts.ListAsync(
            CurrentUserId(),
            workspaceId,
            limit,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result);
    }

    [HttpGet("{artifactId:guid}")]
    public async Task<IActionResult> Get(
        Guid workspaceId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        var result = await artifacts.GetAsync(
            CurrentUserId(),
            workspaceId,
            artifactId,
            cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : MapError(result);
    }
    [HttpGet("{artifactId:guid}/content")]
    public async Task<IActionResult> Content(
        Guid workspaceId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        var result = await artifacts.OpenContentAsync(
            CurrentUserId(),
            workspaceId,
            artifactId,
            cancellationToken);

        if (!result.Succeeded)
            return MapError(result);

        var content = result.Value!;
        return File(
            content.Content,
            content.ContentType,
            content.FileName,
            enableRangeProcessing: false);
    }

    [HttpDelete("{artifactId:guid}")]
    public async Task<IActionResult> Delete(
        Guid workspaceId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        var result = await artifacts.DeleteAsync(
            CurrentUserId(),
            workspaceId,
            artifactId,
            cancellationToken);

        return result.Succeeded
            ? NoContent()
            : MapError(result);
    }

    private IActionResult MapError<T>(
        ArtifactResult<T> result)
    {
        var payload = new
        {
            code = result.ErrorCode
        };

        return result.ErrorCode switch
        {
            "workspace_not_found" or
            "artifact_not_found" =>
                NotFound(payload),

            "forbidden" =>
                StatusCode(
                    StatusCodes.Status403Forbidden,
                    payload),

            "file_too_large" =>
                StatusCode(
                    StatusCodes.Status413PayloadTooLarge,
                    payload),

            "invalid_content_type" =>
                StatusCode(
                    StatusCodes.Status415UnsupportedMediaType,
                    payload),

            "artifact_quota_exceeded" or
            "idempotency_conflict" or
            "artifact_not_ready" =>
                Conflict(payload),

            "artifact_storage_unavailable" =>
                StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    payload),

            "artifact_integrity_failed" or
            "artifact_storage_failed" =>
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
