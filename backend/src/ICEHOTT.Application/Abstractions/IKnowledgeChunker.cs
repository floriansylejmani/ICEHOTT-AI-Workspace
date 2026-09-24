namespace ICEHOTT.Application.Abstractions;

public interface IKnowledgeChunker
{
    IReadOnlyList<string> Chunk(string content);
}
