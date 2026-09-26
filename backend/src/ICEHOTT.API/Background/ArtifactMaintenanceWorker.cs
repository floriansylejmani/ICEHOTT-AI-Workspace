using ICEHOTT.Application.Artifacts;

namespace ICEHOTT.API.Background;

public sealed class ArtifactMaintenanceWorker(
    IServiceScopeFactory scopeFactory,
    ArtifactPolicy policy,
    ILogger<ArtifactMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(
            Math.Clamp(
                policy.MaintenanceIntervalSeconds,
                5,
                3600));

        logger.LogInformation(
            "Artifact maintenance worker started with {IntervalSeconds}s interval.",
            interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var maintenance = scope.ServiceProvider
                    .GetRequiredService<ArtifactMaintenanceService>();

                var result = await maintenance.RunOnceAsync(
                    stoppingToken);

                if (result.RecoveredReady > 0 ||
                    result.MarkedFailed > 0 ||
                    result.MetadataStagingCleaned > 0 ||
                    result.PhysicalObjectsDeleted > 0 ||
                    result.OrphanStagingDeleted > 0)
                {
                    logger.LogInformation(
                        "Artifact maintenance recovered {RecoveredReady}, failed {MarkedFailed}, staging metadata cleaned {MetadataStagingCleaned}, physical objects deleted {PhysicalObjectsDeleted}, orphan staging deleted {OrphanStagingDeleted}.",
                        result.RecoveredReady,
                        result.MarkedFailed,
                        result.MetadataStagingCleaned,
                        result.PhysicalObjectsDeleted,
                        result.OrphanStagingDeleted);
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Artifact maintenance iteration failed.");
            }

            await Task.Delay(
                interval,
                stoppingToken);
        }
    }
}
