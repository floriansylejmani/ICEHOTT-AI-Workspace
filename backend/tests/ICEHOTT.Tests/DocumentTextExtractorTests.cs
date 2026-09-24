using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Infrastructure.Documents;

namespace ICEHOTT.Tests;

public sealed class DocumentTextExtractorTests
{
    private readonly DocumentTextExtractor _extractor = new();

    [Fact]
    public async Task Extracts_Utf8_Text_Files()
    {
        await using var stream = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes("ICEHOTT plain text knowledge"));

        var text = await _extractor.ExtractAsync(
            stream,
            "knowledge.md",
            "text/markdown");

        Assert.Contains("ICEHOTT plain text knowledge", text);
    }

    [Fact]
    public async Task Extracts_Docx_Paragraph_Text()
    {
        await using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(
            stream,
            WordprocessingDocumentType.Document,
            autoSave: true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(
                new Body(
                    new Paragraph(
                        new Run(
                            new Text("DOCX workspace knowledge works")))));
            mainPart.Document.Save();
        }

        stream.Position = 0;
        var text = await _extractor.ExtractAsync(
            stream,
            "policy.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        Assert.Contains("DOCX workspace knowledge works", text);
    }

    [Fact]
    public async Task Extracts_Pdf_Text()
    {
        const string pdfBase64 = "JVBERi0xLjMKJZOMi54gUmVwb3J0TGFiIEdlbmVyYXRlZCBQREYgZG9jdW1lbnQgKG9wZW5zb3VyY2UpCjEgMCBvYmoKPDwKL0YxIDIgMCBSCj4+CmVuZG9iagoyIDAgb2JqCjw8Ci9CYXNlRm9udCAvSGVsdmV0aWNhIC9FbmNvZGluZyAvV2luQW5zaUVuY29kaW5nIC9OYW1lIC9GMSAvU3VidHlwZSAvVHlwZTEgL1R5cGUgL0ZvbnQKPj4KZW5kb2JqCjMgMCBvYmoKPDwKL0NvbnRlbnRzIDcgMCBSIC9NZWRpYUJveCBbIDAgMCAyMDAgMjAwIF0gL1BhcmVudCA2IDAgUiAvUmVzb3VyY2VzIDw8Ci9Gb250IDEgMCBSIC9Qcm9jU2V0IFsgL1BERiAvVGV4dCAvSW1hZ2VCIC9JbWFnZUMgL0ltYWdlSSBdCj4+IC9Sb3RhdGUgMCAvVHJhbnMgPDwKCj4+IAogIC9UeXBlIC9QYWdlCj4+CmVuZG9iago0IDAgb2JqCjw8Ci9QYWdlTW9kZSAvVXNlTm9uZSAvUGFnZXMgNiAwIFIgL1R5cGUgL0NhdGFsb2cKPj4KZW5kb2JqCjUgMCBvYmoKPDwKL0F1dGhvciAoYW5vbnltb3VzKSAvQ3JlYXRpb25EYXRlIChEOjIwMjYwOTI0MTIxMjAwKzAwJzAwJykgL0NyZWF0b3IgKGFub255bW91cykgL0tleXdvcmRzICgpIC9Nb2REYXRlIChEOjIwMjYwOTI0MTIxMjAwKzAwJzAwJykgL1Byb2R1Y2VyIChSZXBvcnRMYWIgUERGIExpYnJhcnkgLSBcKG9wZW5zb3VyY2VcKSkgCiAgL1N1YmplY3QgKHVuc3BlY2lmaWVkKSAvVGl0bGUgKHVudGl0bGVkKSAvVHJhcHBlZCAvRmFsc2UKPj4KZW5kb2JqCjYgMCBvYmoKPDwKL0NvdW50IDEgL0tpZHMgWyAzIDAgUiBdIC9UeXBlIC9QYWdlcwo+PgplbmRvYmoKNyAwIG9iago8PAovTGVuZ3RoIDEwNQo+PgpzdHJlYW0KMSAwIDAgMSAwIDAgY20gIEJUIC9GMSAxMiBUZiAxNC40IFRMIEVUCkJUIDEgMCAwIDEgMjAgMTAwIFRtIChQREYga25vd2xlZGdlIGV4dHJhY3Rpb24gd29ya3MpIFRqIFQqIEVUCiAKZW5kc3RyZWFtCmVuZG9iagp4cmVmCjAgOAowMDAwMDAwMDAwIDY1NTM1IGYgCjAwMDAwMDAwNjEgMDAwMDAgbiAKMDAwMDAwMDA5MiAwMDAwMCBuIAowMDAwMDAwMTk5IDAwMDAwIG4gCjAwMDAwMDAzOTIgMDAwMDAgbiAKMDAwMDAwMDQ2MCAwMDAwMCBuIAowMDAwMDAwNzIxIDAwMDAwIG4gCjAwMDAwMDA3ODAgMDAwMDAgbiAKdHJhaWxlcgo8PAovSUQgCls8MTNkMWUzYWM3Y2VmZGUxODU1YjgwNzNkMTdiZWNjMzM+PDEzZDFlM2FjN2NlZmRlMTg1NWI4MDczZDE3YmVjYzMzPl0KJSBSZXBvcnRMYWIgZ2VuZXJhdGVkIFBERiBkb2N1bWVudCAtLSBkaWdlc3QgKG9wZW5zb3VyY2UpCgovSW5mbyA1IDAgUgovUm9vdCA0IDAgUgovU2l6ZSA4Cj4+CnN0YXJ0eHJlZgo5MzUKJSVFT0YK";
        await using var stream = new MemoryStream(Convert.FromBase64String(pdfBase64));

        var text = await _extractor.ExtractAsync(
            stream,
            "policy.pdf",
            "application/pdf");

        Assert.Contains("PDF knowledge extraction works", text);
    }

    [Fact]
    public async Task Rejects_Unsupported_File_Extensions()
    {
        await using var stream = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<UnsupportedKnowledgeFileException>(() =>
            _extractor.ExtractAsync(stream, "malware.exe", "application/octet-stream"));
    }
}
