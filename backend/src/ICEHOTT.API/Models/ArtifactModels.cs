namespace ICEHOTT.API.Models;

public sealed class UploadArtifactRequest
{
    public IFormFile? File { get; init; }
    public string? IdempotencyKey { get; init; }
}
