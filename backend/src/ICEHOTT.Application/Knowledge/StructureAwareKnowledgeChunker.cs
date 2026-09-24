using System.Text;
using ICEHOTT.Application.Abstractions;

namespace ICEHOTT.Application.Knowledge;

public sealed class StructureAwareKnowledgeChunker : IKnowledgeChunker
{
    private const int TargetWords = 450;
    private const int OverlapWords = 80;

    public IReadOnlyList<string> Chunk(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return [];

        var blocks = ReadBlocks(normalized);
        var chunks = new List<string>();
        var current = new List<string>();
        var currentWords = 0;

        foreach (var block in blocks)
        {
            var blockWords = CountWords(block);
            if (blockWords > TargetWords)
            {
                FlushCurrent(current, chunks);
                currentWords = 0;
                chunks.AddRange(SplitOversizedBlock(block));
                continue;
            }

            if (current.Count > 0 && currentWords + blockWords > TargetWords)
            {
                var completed = string.Join("\n\n", current).Trim();
                chunks.Add(completed);

                var overlap = TailWords(completed, OverlapWords);
                current.Clear();
                currentWords = 0;
                if (!string.IsNullOrWhiteSpace(overlap))
                {
                    current.Add(overlap);
                    currentWords = CountWords(overlap);
                }
            }

            current.Add(block);
            currentWords += blockWords;
        }

        FlushCurrent(current, chunks);
        return chunks
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadBlocks(string content)
    {
        var blocks = new List<string>();
        var builder = new StringBuilder();
        var inFence = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence && string.IsNullOrWhiteSpace(line))
            {
                FlushBlock(builder, blocks);
                continue;
            }

            if (builder.Length > 0) builder.AppendLine();
            builder.Append(line);
        }

        FlushBlock(builder, blocks);
        return blocks;
    }

    private static IReadOnlyList<string> SplitOversizedBlock(string block)
    {
        var words = block.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();
        var start = 0;

        while (start < words.Length)
        {
            var count = Math.Min(TargetWords, words.Length - start);
            chunks.Add(string.Join(' ', words, start, count));
            if (start + count >= words.Length) break;
            start += TargetWords - OverlapWords;
        }

        return chunks;
    }

    private static string TailWords(string text, int count)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= count) return text;
        return string.Join(' ', words, words.Length - count, count);
    }

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static void FlushBlock(StringBuilder builder, ICollection<string> blocks)
    {
        if (builder.Length == 0) return;
        blocks.Add(builder.ToString().Trim());
        builder.Clear();
    }

    private static void FlushCurrent(List<string> current, ICollection<string> chunks)
    {
        if (current.Count == 0) return;
        chunks.Add(string.Join("\n\n", current).Trim());
        current.Clear();
    }
}
