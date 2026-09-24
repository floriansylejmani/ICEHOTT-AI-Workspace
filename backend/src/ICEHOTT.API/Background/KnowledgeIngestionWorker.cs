using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Knowledge;
using Microsoft.Extensions.Options;

namespace ICEHOTT.API.Background;

public sealed class KnowledgeIngestionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<KnowledgeWorkerOptions> options,
    TimeProvider clock,
    ILogger<KnowledgeIngestionWorker> logger) : BackgroundService
{
    private readonly KnowledgeWorkerOptions _options = options.Value;
    private readonly string _workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Knowledge ingestion worker is disabled.");
            return;
        }

        var pollDelay = TimeSpan.FromMilliseconds(
            Math.Clamp(_options.PollMilliseconds, 100, 10_000));
        var leaseDuration = TimeSpan.FromSeconds(
            Math.Clamp(_options.LeaseSeconds, 30, 1800));

        logger.LogInformation(
            "Knowledge ingestion worker {WorkerId} started with {LeaseSeconds}s leases.",
            _workerId,
            leaseDuration.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IKnowledgeJobQueue>();
                var lease = await queue.LeaseNextAsync(
                    _workerId,
                    leaseDuration,
                    stoppingToken);

                if (lease is null)
                {
                    await Task.Delay(pollDelay, stoppingToken);
                    continue;
                }

                await ProcessLeaseAsync(lease, leaseDuration, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Knowledge ingestion worker loop failed.");
                await Task.Delay(pollDelay, stoppingToken);
            }
        }
    }

    private async Task ProcessLeaseAsync(
        KnowledgeJobLease lease,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        using var processingScope = scopeFactory.CreateScope();
        var queue = processingScope.ServiceProvider.GetRequiredService<IKnowledgeJobQueue>();
        var processor = processingScope.ServiceProvider.GetRequiredService<KnowledgeIndexingProcessor>();

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = RenewLeaseLoopAsync(
            lease.Id,
            lease.WorkerId,
            leaseDuration,
            heartbeatCts.Token);

        try
        {
            var chunkCount = await processor.ProcessAsync(
                lease.WorkspaceId,
                lease.DocumentId,
                cancellationToken);

            heartbeatCts.Cancel();
            await IgnoreCancellationAsync(heartbeat);

            var completed = await queue.CompleteAsync(
                lease.Id,
                lease.WorkerId,
                cancellationToken);

            if (!completed)
            {
                logger.LogWarning(
                    "Knowledge job {JobId} finished processing but lease ownership was lost before completion.",
                    lease.Id);
                return;
            }

            RagTelemetry.JobsCompleted.Add(1);

            logger.LogInformation(
                "Knowledge job {JobId} completed for document {DocumentId} with {ChunkCount} chunks.",
                lease.Id,
                lease.DocumentId,
                chunkCount);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            heartbeatCts.Cancel();
            await IgnoreCancellationAsync(heartbeat);

            var knowledge = processingScope.ServiceProvider.GetRequiredService<IKnowledgeRepository>();
            var document = await knowledge.FindDocumentAsync(
                lease.WorkspaceId,
                lease.DocumentId,
                cancellationToken);

            var error = exception.GetType().Name + ": " + exception.Message;
            var unitOfWork = processingScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            if (lease.Attempts >= lease.MaxAttempts)
            {
                var failed = await queue.FailAsync(
                    lease.Id,
                    lease.WorkerId,
                    error,
                    cancellationToken);

                if (!failed)
                {
                    logger.LogWarning(
                        "Knowledge job {JobId} failure was ignored because lease ownership was lost.",
                        lease.Id);
                    return;
                }

                if (document is not null)
                {
                    document.MarkFailed();
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                }

                RagTelemetry.JobsFailed.Add(1);

                logger.LogError(
                    exception,
                    "Knowledge job {JobId} permanently failed after {Attempts} attempts.",
                    lease.Id,
                    lease.Attempts);
                return;
            }

            var delaySeconds = Math.Min(
                Math.Max(1, _options.MaxRetryDelaySeconds),
                Math.Pow(2, Math.Clamp(lease.Attempts, 1, 12)));
            var retryAt = clock.GetUtcNow().AddSeconds(delaySeconds);

            var retried = await queue.RetryAsync(
                lease.Id,
                lease.WorkerId,
                error,
                retryAt,
                cancellationToken);

            if (!retried)
            {
                logger.LogWarning(
                    "Knowledge job {JobId} retry was ignored because lease ownership was lost.",
                    lease.Id);
                return;
            }

            if (document is not null)
            {
                document.MarkQueued();
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            RagTelemetry.JobsRetried.Add(1);

            logger.LogWarning(
                exception,
                "Knowledge job {JobId} will retry at {RetryAtUtc}; attempt {Attempt}/{MaxAttempts}.",
                lease.Id,
                retryAt,
                lease.Attempts,
                lease.MaxAttempts);
        }
    }

    private async Task RenewLeaseLoopAsync(
        Guid jobId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var heartbeatDelay = TimeSpan.FromMilliseconds(
            Math.Max(5_000, leaseDuration.TotalMilliseconds / 3));

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(heartbeatDelay, cancellationToken);

            using var scope = scopeFactory.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<IKnowledgeJobQueue>();
            var renewed = await queue.RenewLeaseAsync(
                jobId,
                workerId,
                leaseDuration,
                cancellationToken);

            if (!renewed)
            {
                logger.LogWarning(
                    "Knowledge job {JobId} lease could not be renewed by {WorkerId}.",
                    jobId,
                    workerId);
                return;
            }
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
