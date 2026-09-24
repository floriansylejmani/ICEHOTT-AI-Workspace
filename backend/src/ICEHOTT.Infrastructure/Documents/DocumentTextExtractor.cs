using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ICEHOTT.Application.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ICEHOTT.Infrastructure.Documents;

public sealed class DocumentTextExtractor : IDocumentTextExtractor
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json"
    };

    public async Task<string> ExtractAsync(
        Stream stream,
        string fileName,
        string? contentType,
        CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
            throw new UnsupportedKnowledgeFileException("A supported file extension is required.");

        try
        {
            if (TextExtensions.Contains(extension))
                return await ReadTextAsync(stream, cancellationToken);

            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                return ReadPdf(stream);

            if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
                return ReadDocx(stream);

            throw new UnsupportedKnowledgeFileException(
                $"Unsupported knowledge file type '{extension}'.");
        }
        catch (UnsupportedKnowledgeFileException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidKnowledgeFileException(
                $"Could not extract text from '{Path.GetFileName(fileName)}'.",
                exception);
        }
    }

    private static async Task<string> ReadTextAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        if (stream.CanSeek) stream.Position = 0;
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);

        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string ReadPdf(Stream stream)
    {
        if (stream.CanSeek) stream.Position = 0;
        using var document = PdfDocument.Open(stream);

        var pages = new List<string>(document.NumberOfPages);
        foreach (var page in document.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page).Trim();
            if (!string.IsNullOrWhiteSpace(text)) pages.Add(text);
        }

        return string.Join(Environment.NewLine + Environment.NewLine, pages);
    }

    private static string ReadDocx(Stream stream)
    {
        if (stream.CanSeek) stream.Position = 0;
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null) return string.Empty;

        var paragraphs = body
            .Descendants<Paragraph>()
            .Select(paragraph => paragraph.InnerText.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text));

        return string.Join(Environment.NewLine, paragraphs);
    }
}
