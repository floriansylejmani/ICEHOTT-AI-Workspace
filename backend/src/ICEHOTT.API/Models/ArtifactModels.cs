namespace ICEHOTT.API.Models;

public sealed class UploadArtifactRequest
{
    public IFormFile? File { get; init; }
    public string? IdempotencyKey { get; init; }
    public Guid? WorkflowRunId { get; init; }
    public Guid? StepRunId { get; init; }
}
