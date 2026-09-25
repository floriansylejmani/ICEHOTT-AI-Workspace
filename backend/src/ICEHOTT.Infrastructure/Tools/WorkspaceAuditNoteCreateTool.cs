using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Tools;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Infrastructure.Tools;

public sealed class WorkspaceAuditNoteCreateTool(
    IWorkspaceAuditNoteRepository notes,
    TimeProvider clock) : IWorkspaceTool
{
    public ToolDefinition Definition { get; } = new(
        "workspace.audit-note.create",
        "Create a workspace audit note after human approval.",
        ToolRiskLevel.SensitiveWrite,
        WorkspaceRole.Admin,
        RequiresApproval: true,
        MinimumApproverRole: WorkspaceRole.Admin,
        [
            new ToolArgumentDefinition(
                "message",
                ToolArgumentType.String,
                Required: true,
                MaxLength: 500)
        ]);

    public ToolArgumentValidationResult ValidateArguments(
        JsonElement arguments) =>
        ToolArgumentReader.ValidateSingleRequiredString(
            arguments,
            "message",
            500);

    public async Task<ToolExecutionOutput> ExecuteAsync(
        ToolExecutionContext context,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var message = ToolArgumentReader.RequiredString(
            arguments,
            "message");

        var note = new WorkspaceAuditNote(
            Guid.NewGuid(),
            context.WorkspaceId,
            context.ExecutionId,
            context.RequestedByUserId,
            message,
            clock.GetUtcNow());

        await notes.AddAsync(note, cancellationToken);

        return new ToolExecutionOutput(
            JsonSerializer.Serialize(new
            {
                noteId = note.Id,
                createdAtUtc = note.CreatedAtUtc
            }));
    }
}
