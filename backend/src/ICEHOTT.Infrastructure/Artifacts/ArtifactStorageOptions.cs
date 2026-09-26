namespace ICEHOTT.Infrastructure.Artifacts;

public sealed class ArtifactStorageOptions
{
    public const string SectionName = "ArtifactStorage";

    public string RootPath { get; init; } = "data/artifacts";
    public long MaxArtifactBytes { get; init; } = 25L * 1024 * 1024;
    public long MaxWorkspaceBytes { get; init; } = 500L * 1024 * 1024;
    public int MaxArtifactsPerWorkspace { get; init; } = 1000;
    public int MaintenanceIntervalSeconds { get; init; } = 60;
    public int PendingRecoverySeconds { get; init; } = 300;
    public int StagingRetentionMinutes { get; init; } = 60;
    public bool MaintenanceEnabled { get; init; } = true;
}
