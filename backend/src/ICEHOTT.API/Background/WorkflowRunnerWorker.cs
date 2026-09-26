using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Workflows;
using Microsoft.Extensions.Options;

namespace ICEHOTT.API.Background;

public sealed class WorkflowRunnerWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkflowRunnerOptions> options,
    ILogger<WorkflowRunnerWorker> logger) : BackgroundService
{
    private readonly WorkflowRunnerOptions _options = options.Value;
    private readonly Guid _workerId = Guid.NewGuid();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Workflow runner is disabled.");
            return;
        }

        var pollDelay = TimeSpan.FromMilliseconds(
            Math.Clamp(_options.PollMilliseconds, 100, 10_000));
        var leaseDuration = TimeSpan.FromSeconds(
            Math.Clamp(_options.LeaseSeconds, 30, 1800));

        logger.LogInformation(
            "Workflow runner {WorkerId} started with {LeaseSeconds}s leases.",
            _workerId,
            leaseDuration.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IWorkflowRunQueue>();
                var lease = await queue.LeaseNextAsync(
                    _workerId,
                    leaseDuration,
                    stoppingToken);

                if (lease is null)
                {
                    await Task.Delay(pollDelay, stoppingToken);
                    continue;
                }

                await ProcessLeaseAsync(
                    lease,
                    leaseDuration,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Workflow runner loop failed.");
                await Task.Delay(pollDelay, stoppingToken);
            }
        }
    }
    private async Task ProcessLeaseAsync(
        WorkflowRunLease lease,
        TimeSpan leaseDuration,
        CancellationToken stoppingToken)
    {
        using var processingScope = scopeFactory.CreateScope();
        var processor = processingScope.ServiceProvider
            .GetRequiredService<WorkflowRunProcessor>();

        using var processingCts =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var heartbeatCts =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        var heartbeat = RenewLeaseLoopAsync(
            lease,
            leaseDuration,
            processingCts,
            heartbeatCts.Token);

        try
        {
            var result = await processor.ProcessAsync(
                lease,
                processingCts.Token);

            heartbeatCts.Cancel();
            await IgnoreCancellationAsync(heartbeat);

            if (result.Disposition == WorkflowRunProcessDisposition.LeaseLost)
            {
                logger.LogWarning(
                    "Workflow run {RunId} lost lease generation {LeaseGeneration} before commit.",
                    lease.RunId,
                    lease.LeaseGeneration);
                return;
            }

            logger.LogInformation(
                "Workflow run {RunId} processor yielded {Disposition} with status {Status}.",
                lease.RunId,
                result.Disposition,
                result.Status);
        }
        catch (OperationCanceledException)
            when (!stoppingToken.IsCancellationRequested &&
                  processingCts.IsCancellationRequested)
        {
            heartbeatCts.Cancel();
            await IgnoreCancellationAsync(heartbeat);

            logger.LogWarning(
                "Workflow run {RunId} processing stopped after lease ownership was lost.",
                lease.RunId);
        }
        finally
        {
            heartbeatCts.Cancel();
            await IgnoreCancellationAsync(heartbeat);
        }
    }

    private async Task RenewLeaseLoopAsync(
        WorkflowRunLease lease,
        TimeSpan leaseDuration,
        CancellationTokenSource processingCts,
        CancellationToken cancellationToken)
    {
        var heartbeatDelay = TimeSpan.FromMilliseconds(
            Math.Max(5_000, leaseDuration.TotalMilliseconds / 3));

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(heartbeatDelay, cancellationToken);

            using var scope = scopeFactory.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<IWorkflowRunQueue>();
            var renewed = await queue.RenewLeaseAsync(
                lease.RunId,
                lease.WorkerId,
                lease.LeaseGeneration,
                leaseDuration,
                cancellationToken);

            if (renewed)
                continue;

            logger.LogWarning(
                "Workflow run {RunId} lease generation {LeaseGeneration} could not be renewed.",
                lease.RunId,
                lease.LeaseGeneration);
            processingCts.Cancel();
            return;
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
