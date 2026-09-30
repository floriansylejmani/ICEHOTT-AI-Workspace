using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Observability;
using ICEHOTT.Application.Workflows;
using Microsoft.Extensions.Options;

namespace ICEHOTT.API.Background;

public sealed class WorkflowSchedulerWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkflowSchedulerOptions> options,
    TimeProvider clock,
    ILogger<WorkflowSchedulerWorker> logger) : BackgroundService
{
    private readonly WorkflowSchedulerOptions _options = options.Value;

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Workflow scheduler is disabled.");
            return;
        }

        var pollDelay = TimeSpan.FromMilliseconds(
            Math.Clamp(
                _options.PollMilliseconds,
                100,
                60_000));
        var maxClaims = Math.Clamp(
            _options.MaxClaimsPerCycle,
            1,
            100);

        logger.LogInformation(
            "Workflow scheduler started with {PollMilliseconds}ms polling and max {MaxClaims} actions per cycle.",
            pollDelay.TotalMilliseconds,
            maxClaims);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var didWork = false;

                for (var i = 0; i < maxClaims; i++)
                {
                    using var scope = scopeFactory.CreateScope();
                    var store = scope.ServiceProvider
                        .GetRequiredService<IWorkflowTriggerSchedulerStore>();
                    var now = clock.GetUtcNow();

                    var processed = await store.ProcessNextClaimedAsync(
                        now,
                        stoppingToken);

                    if (processed.DidWork)
                    {
                        didWork = true;
                        LogResult(processed);
                        continue;
                    }

                    var claimed = await store.ClaimNextDueAsync(
                        now,
                        stoppingToken);

                    if (!claimed.DidWork)
                        break;

                    didWork = true;
                    LogResult(claimed);
                }

                if (!didWork)
                    await Task.Delay(pollDelay, stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                IcehottMetrics.SchedulerLoopFailures.Add(1);
                logger.LogError(
                    exception,
                    "Workflow scheduler loop failed.");
                await Task.Delay(pollDelay, stoppingToken);
            }
        }
    }

    private void LogResult(
        WorkflowTriggerSchedulerResult result)
    {
        IcehottMetrics.SchedulerActions.Add(
            1,
            IcehottMetrics.Tag("disposition", result.Disposition.ToString().ToLowerInvariant()));

        if (result.Disposition ==
            WorkflowTriggerSchedulerDisposition.Failed)
        {
            logger.LogWarning(
                "Workflow trigger {TriggerId} fire {FireId} failed closed: {Reason}.",
                result.TriggerId,
                result.FireId,
                result.Reason);
            return;
        }

        logger.LogInformation(
            "Workflow scheduler action {Disposition} for trigger {TriggerId}, fire {FireId}, run {RunId}.",
            result.Disposition,
            result.TriggerId,
            result.FireId,
            result.WorkflowRunId);
    }
}
