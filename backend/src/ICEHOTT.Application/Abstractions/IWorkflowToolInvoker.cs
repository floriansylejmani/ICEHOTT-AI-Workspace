using System.Text.Json;
using ICEHOTT.Application.Tools;

namespace ICEHOTT.Application.Abstractions;

public interface IWorkflowToolInvoker
{
    Task<ToolOperationResult<ToolExecutionView>> RequestAsync(
        Guid userId,
        Guid workspaceId,
        string toolName,
        JsonElement arguments,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<ToolOperationResult<ToolExecutionView>> GetAsync(
        Guid userId,
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default);

    Task<ToolOperationResult<ToolExecutionView>> CancelAsync(
        Guid userId,
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default);
}
