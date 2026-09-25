using System.Text.Json;
using ICEHOTT.Application.Tools;

namespace ICEHOTT.Application.Abstractions;

public interface IWorkspaceTool
{
    ToolDefinition Definition { get; }

    ToolArgumentValidationResult ValidateArguments(JsonElement arguments);

    Task<ToolExecutionOutput> ExecuteAsync(
        ToolExecutionContext context,
        JsonElement arguments,
        CancellationToken cancellationToken = default);
}
