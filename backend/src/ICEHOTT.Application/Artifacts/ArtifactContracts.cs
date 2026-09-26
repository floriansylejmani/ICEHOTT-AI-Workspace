using ICEHOTT.Domain.Workflows;

namespace ICEHOTT.Application.Artifacts;

public sealed record ArtifactPolicy(
    long MaxArtifactBytes,
    long MaxWorkspaceBytes,
    int MaxArtifactsPerWorkspace,
    int MaintenanceIntervalSeconds,
    int PendingRecoverySeconds,
    int StagingRetentionMinutes)
{
    public const long AbsoluteMaxArtifactBytes = 100L * 1024 * 1024;
    public const long AbsoluteMaxWorkspaceBytes = 100L * 1024 * 1024 * 1024;
    public const int AbsoluteMaxArtifactsPerWorkspace = 100_000;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (MaxArtifactBytes is < 1 or > AbsoluteMaxArtifactBytes)
            errors.Add(
                $"Artifact max bytes must be between 1 and {AbsoluteMaxArtifactBytes}.");

        if (MaxWorkspaceBytes is < 1 or > AbsoluteMaxWorkspaceBytes)
            errors.Add(
                $"Artifact workspace max bytes must be between 1 and {AbsoluteMaxWorkspaceBytes}.");

        if (MaxWorkspaceBytes < MaxArtifactBytes)
            errors.Add("Artifact workspace quota must be at least one artifact.");

        if (MaxArtifactsPerWorkspace is < 1 or > AbsoluteMaxArtifactsPerWorkspace)
            errors.Add(
                $"Artifact count must be between 1 and {AbsoluteMaxArtifactsPerWorkspace}.");

        if (MaintenanceIntervalSeconds is < 5 or > 3600)
            errors.Add("Artifact maintenance interval must be 5-3600 seconds.");

        if (PendingRecoverySeconds is < 30 or > 86_400)
            errors.Add("Artifact pending recovery age must be 30-86400 seconds.");

        if (StagingRetentionMinutes is < 1 or > 10_080)
            errors.Add("Artifact staging retention must be 1-10080 minutes.");

        return errors;
    }
}

public sealed record ArtifactView(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    ArtifactStatus Status,
    Guid CreatedByUserId,
    Guid? WorkflowRunId,
    Guid? StepRunId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? FailedAtUtc,
    DateTimeOffset? DeletedAtUtc);

public sealed record ArtifactContentHandle(
    Stream Content,
    string FileName,
    string ContentType,
    long SizeBytes);

public sealed record ArtifactResult<T>(
    T? Value,
    string? ErrorCode,
    bool IsReplay = false)
{
    public bool Succeeded => ErrorCode is null;
}

public sealed record ArtifactMaintenanceResult(
    int RecoveredReady,
    int MarkedFailed,
    int MetadataStagingCleaned,
    int PhysicalObjectsDeleted,
    int OrphanStagingDeleted);
