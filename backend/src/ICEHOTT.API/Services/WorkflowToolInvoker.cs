using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Tools;

namespace ICEHOTT.API.Services;

public sealed class WorkflowToolInvoker(
    IServiceScopeFactory scopeFactory) : IWorkflowToolInvoker
{
    public async Task<ToolOperationResult<ToolExecutionView>> RequestAsync(
        Guid userId,
        Guid workspaceId,
        string toolName,
        JsonElement arguments,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ToolExecutionService>();
        return await service.RequestAsync(
            userId,
            workspaceId,
            toolName,
            arguments,
            idempotencyKey,
            cancellationToken);
    }

    public async Task<ToolOperationResult<ToolExecutionView>> GetAsync(
        Guid userId,
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ToolExecutionService>();
        return await service.GetAsync(
            userId,
            workspaceId,
            executionId,
            cancellationToken);
    }

    public async Task<ToolOperationResult<ToolExecutionView>> CancelAsync(
        Guid userId,
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ToolExecutionService>();
        return await service.CancelAsync(
            userId,
            workspaceId,
            executionId,
            cancellationToken);
    }
}
