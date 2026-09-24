using System.Text.RegularExpressions;
using ICEHOTT.Application.Abstractions;

namespace ICEHOTT.Application.Knowledge;

public sealed partial class RetrievedContentPolicy : IRetrievedContentPolicy
{
    public RetrievedContentDecision Evaluate(string content)
    {
        var normalized = WhitespaceRegex().Replace(content, " ").Trim();

        if (InstructionOverrideRegex().IsMatch(normalized))
            return new(false, "instruction_override");
        if (PromptExfiltrationRegex().IsMatch(normalized))
            return new(false, "prompt_exfiltration");
        if (UnsafeToolInstructionRegex().IsMatch(normalized))
            return new(false, "unsafe_tool_instruction");

        return new(true, null);
    }

    [GeneratedRegex(
        @"\b(ignore|disregard|override|forget)\b.{0,60}\b(previous|prior|system|developer)\b.{0,30}\b(instruction|message|rule|prompt)s?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstructionOverrideRegex();

    [GeneratedRegex(
        @"\b(reveal|show|print|expose|leak)\b.{0,40}\b(system|developer)\b.{0,20}\b(prompt|message|instruction)s?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PromptExfiltrationRegex();

    [GeneratedRegex(
        @"\b(execute|invoke|call|run|send|delete)\b.{0,35}\b(tool|command|email|file|data)\b.{0,40}\b(without|bypass|ignore|no approval)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeToolInstructionRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
