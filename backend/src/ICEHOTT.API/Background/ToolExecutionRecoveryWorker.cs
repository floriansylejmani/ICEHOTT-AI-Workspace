using ICEHOTT.Application.Tools;

namespace ICEHOTT.API.Background;

/// <summary>
/// Finds abandoned tool executions. It never invokes a handler or replays
/// an external write; an expired lease becomes OutcomeUnknown for review.
/// </summary>
public sealed class ToolExecutionRecoveryWorker(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<ToolExecutionRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(15), clock);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var recovery = scope.ServiceProvider
                        .GetRequiredService<ToolExecutionRecoveryService>();
                    var count = await recovery.RecoverExpiredAsync(
                        clock.GetUtcNow(), stoppingToken);
                    if (count > 0)
                        logger.LogWarning(
                            "Marked {Count} expired tool executions OutcomeUnknown.",
                            count);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    // Provider exceptions can contain connection details.
                    logger.LogWarning(
                        "Tool execution recovery pass failed; will retry.");
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
