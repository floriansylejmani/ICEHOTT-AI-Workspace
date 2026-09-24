using Microsoft.AspNetCore.Http;

namespace ICEHOTT.API.Models;

public sealed record IngestKnowledgeRequest(
    string Title,
    string? SourceName,
    string Content);

public sealed class UploadKnowledgeDocumentRequest
{
    public string? Title { get; init; }
    public IFormFile? File { get; init; }
}
