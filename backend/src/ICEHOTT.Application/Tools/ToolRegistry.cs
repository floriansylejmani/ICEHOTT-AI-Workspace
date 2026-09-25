using ICEHOTT.Application.Abstractions;

namespace ICEHOTT.Application.Tools;

public interface IToolRegistry
{
    IReadOnlyList<IWorkspaceTool> All { get; }
    IWorkspaceTool? Find(string toolName);
}

public sealed class ToolRegistry : IToolRegistry
{
    private readonly IReadOnlyDictionary<string, IWorkspaceTool> _tools;

    public ToolRegistry(IEnumerable<IWorkspaceTool> tools)
    {
        var items = tools.ToArray();
        var duplicate = items
            .GroupBy(x => x.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
            throw new InvalidOperationException(
                $"Duplicate tool registration: {duplicate.Key}.");

        _tools = items.ToDictionary(
            x => x.Definition.Name,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IWorkspaceTool> All =>
        _tools.Values
            .OrderBy(x => x.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IWorkspaceTool? Find(string toolName) =>
        string.IsNullOrWhiteSpace(toolName)
            ? null
            : _tools.GetValueOrDefault(toolName.Trim());
}
