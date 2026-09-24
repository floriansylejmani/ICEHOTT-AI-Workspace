namespace ICEHOTT.Application.Abstractions;

public interface IDocumentTextExtractor
{
    Task<string> ExtractAsync(
        Stream stream,
        string fileName,
        string? contentType,
        CancellationToken cancellationToken = default);
}

public sealed class UnsupportedKnowledgeFileException(string message) : Exception(message);

public sealed class InvalidKnowledgeFileException(string message, Exception? innerException = null)
    : Exception(message, innerException);
