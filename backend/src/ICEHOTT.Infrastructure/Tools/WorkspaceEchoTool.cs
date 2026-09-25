using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Tools;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Infrastructure.Tools;

public sealed class WorkspaceEchoTool : IWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        "workspace.echo",
        "Echo validated workspace text without external side effects.",
        ToolRiskLevel.ReadOnly,
        WorkspaceRole.Member,
        RequiresApproval: false,
        MinimumApproverRole: null,
        [
            new ToolArgumentDefinition(
                "text",
                ToolArgumentType.String,
                Required: true,
                MaxLength: 500)
        ]);

    public ToolArgumentValidationResult ValidateArguments(
        JsonElement arguments) =>
        ToolArgumentReader.ValidateSingleRequiredString(
            arguments,
            "text",
            500);

    public Task<ToolExecutionOutput> ExecuteAsync(
        ToolExecutionContext context,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var text = ToolArgumentReader.RequiredString(
            arguments,
            "text");

        var result = JsonSerializer.Serialize(new
        {
            text,
            workspaceId = context.WorkspaceId
        });

        return Task.FromResult(new ToolExecutionOutput(result));
    }
}
