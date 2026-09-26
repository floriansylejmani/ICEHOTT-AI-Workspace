using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Workflows;

namespace ICEHOTT.Application.Artifacts;

public sealed class ArtifactMaintenanceService(
    IArtifactRepository artifacts,
    IArtifactStore store,
    IWorkflowAuditRepository audit,
    IUnitOfWork unitOfWork,
    ArtifactPolicy policy,
    TimeProvider clock)
{
    public async Task<ArtifactMaintenanceResult> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var pendingOlderThan = now.AddSeconds(
            -policy.PendingRecoverySeconds);

        var candidates = await artifacts.ListMaintenanceCandidatesAsync(
            pendingOlderThan,
            200,
            cancellationToken);

        var recoveredReady = 0;
        var markedFailed = 0;
        var stagingCleaned = 0;
        var physicalDeleted = 0;

        foreach (var artifact in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (artifact.Status)
            {
                case ArtifactStatus.Pending:
                    {
                        var result = await RecoverPendingAsync(
                            artifact,
                            cancellationToken);

                        if (result == PendingRecoveryOutcome.Ready)
                            recoveredReady++;
                        else if (result == PendingRecoveryOutcome.Failed)
                            markedFailed++;

                        break;
                    }

                case ArtifactStatus.Failed:
                    if (artifact.StagingKey is not null &&
                        await TryDeleteStagingAsync(
                            artifact.StagingKey,
                            cancellationToken))
                    {
                        artifact.MarkStagingCleaned();
                        await unitOfWork.SaveChangesAsync(
                            cancellationToken);
                        stagingCleaned++;
                    }
                    break;

                case ArtifactStatus.Deleted:
                    if (artifact.StorageDeletedAtUtc is null &&
                        await TryDeletePhysicalAsync(
                            artifact,
                            cancellationToken))
                    {
                        artifact.MarkStorageDeleted(
                            clock.GetUtcNow());
                        await unitOfWork.SaveChangesAsync(
                            cancellationToken);
                        physicalDeleted++;
                    }
                    break;
            }
        }

        var orphanStagingDeleted = 0;
        try
        {
            orphanStagingDeleted = await store.CleanupStagingAsync(
                now.AddMinutes(-policy.StagingRetentionMinutes),
                500,
                cancellationToken);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (ArtifactStoreIntegrityException)
        {
        }

        return new(
            recoveredReady,
            markedFailed,
            stagingCleaned,
            physicalDeleted,
            orphanStagingDeleted);
    }
    private async Task<PendingRecoveryOutcome> RecoverPendingAsync(
        Artifact artifact,
        CancellationToken cancellationToken)
    {
        try
        {
            if (artifact.StagingKey is not null)
            {
                await store.CommitAsync(
                    artifact.StagingKey,
                    artifact.StorageKey,
                    artifact.SizeBytes,
                    artifact.Sha256,
                    cancellationToken);
            }
            else
            {
                var info = await store.GetInfoAsync(
                    artifact.StorageKey,
                    cancellationToken);

                if (info is null)
                    return await FailPendingAsync(
                        artifact,
                        cancellationToken);

                if (info.SizeBytes != artifact.SizeBytes ||
                    !string.Equals(
                        info.Sha256,
                        artifact.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                    return await FailPendingAsync(
                        artifact,
                        cancellationToken);
            }

            artifact.MarkReady();

            await audit.AddAsync(
                new WorkflowAuditEvent(
                    Guid.NewGuid(),
                    artifact.WorkspaceId,
                    WorkflowAuditEventType.ArtifactCreated,
                    clock.GetUtcNow(),
                    workflowRunId: artifact.WorkflowRunId,
                    workflowStepRunId: artifact.StepRunId,
                    actorUserId: null,
                    detailJson: JsonSerializer.Serialize(new
                    {
                        artifactId = artifact.Id,
                        recovered = true
                    })),
                cancellationToken);

            await unitOfWork.SaveChangesAsync(
                cancellationToken);

            return PendingRecoveryOutcome.Ready;
        }
        catch (FileNotFoundException)
        {
            return await FailPendingAsync(
                artifact,
                cancellationToken);
        }
        catch (ArtifactStoreIntegrityException)
        {
            return await FailPendingAsync(
                artifact,
                cancellationToken);
        }
        catch (IOException)
        {
            return PendingRecoveryOutcome.Deferred;
        }
        catch (UnauthorizedAccessException)
        {
            return PendingRecoveryOutcome.Deferred;
        }
    }

    private async Task<PendingRecoveryOutcome> FailPendingAsync(
        Artifact artifact,
        CancellationToken cancellationToken)
    {
        artifact.MarkFailed(clock.GetUtcNow());

        if (artifact.StagingKey is not null &&
            await TryDeleteStagingAsync(
                artifact.StagingKey,
                cancellationToken))
        {
            artifact.MarkStagingCleaned();
        }

        await unitOfWork.SaveChangesAsync(
            cancellationToken);

        return PendingRecoveryOutcome.Failed;
    }

    private async Task<bool> TryDeletePhysicalAsync(
        Artifact artifact,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.DeleteAsync(
                artifact.StorageKey,
                cancellationToken);

            if (artifact.StagingKey is not null)
            {
                await store.DeleteStagingAsync(
                    artifact.StagingKey,
                    cancellationToken);
            }

            return await store.GetInfoAsync(
                    artifact.StorageKey,
                    cancellationToken) is null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ArtifactStoreIntegrityException)
        {
            return false;
        }
    }

    private async Task<bool> TryDeleteStagingAsync(
        string stagingKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.DeleteStagingAsync(
                stagingKey,
                cancellationToken);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ArtifactStoreIntegrityException)
        {
            return false;
        }
    }

    private enum PendingRecoveryOutcome
    {
        Deferred = 0,
        Ready = 1,
        Failed = 2
    }
}
