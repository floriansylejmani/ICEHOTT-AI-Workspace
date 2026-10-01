using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Observability;

namespace ICEHOTT.Infrastructure.Artifacts;

/// <summary>
/// Records provider-neutral operation metrics around any <see cref="IArtifactStore"/>.
/// It never inspects keys, sizes or content, adds no exception detail to telemetry, and
/// always rethrows the original exception so behaviour is unchanged.
/// </summary>
public sealed class InstrumentedArtifactStore(IArtifactStore inner, string provider) : IArtifactStore
{
    public IArtifactStore Inner { get; } = inner;

    public Task<ArtifactStageResult> StageAsync(
        Guid workspaceId, Guid artifactId, Stream source, long maxBytes,
        CancellationToken cancellationToken = default) =>
        Measure("stage", () => Inner.StageAsync(workspaceId, artifactId, source, maxBytes, cancellationToken));

    public Task CommitAsync(
        string stagingKey, string storageKey, long expectedSizeBytes, string expectedSha256,
        CancellationToken cancellationToken = default) =>
        Measure("commit", async () =>
        {
            await Inner.CommitAsync(stagingKey, storageKey, expectedSizeBytes, expectedSha256, cancellationToken);
            return true;
        });

    public Task<ArtifactStoredObjectInfo?> GetInfoAsync(
        string storageKey, CancellationToken cancellationToken = default) =>
        Measure("info", () => Inner.GetInfoAsync(storageKey, cancellationToken));

    public Task<Stream?> OpenReadAsync(
        string storageKey, CancellationToken cancellationToken = default) =>
        Measure("read", () => Inner.OpenReadAsync(storageKey, cancellationToken));

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Measure("delete", async () =>
        {
            await Inner.DeleteAsync(storageKey, cancellationToken);
            return true;
        });

    public Task DeleteStagingAsync(string stagingKey, CancellationToken cancellationToken = default) =>
        Measure("delete_staging", async () =>
        {
            await Inner.DeleteStagingAsync(stagingKey, cancellationToken);
            return true;
        });

    public Task<int> CleanupStagingAsync(
        DateTimeOffset olderThanUtc, int maxItems, CancellationToken cancellationToken = default) =>
        Measure("cleanup_staging", () => Inner.CleanupStagingAsync(olderThanUtc, maxItems, cancellationToken));

    private async Task<T> Measure<T>(string operation, Func<Task<T>> action)
    {
        try
        {
            var result = await action();
            Record(operation, "success");
            return result;
        }
        catch (Exception exception)
        {
            Record(operation, Classify(exception));
            if (exception is ArtifactStoreUnavailableException)
                IcehottMetrics.ArtifactStoreUnavailable.Add(
                    1,
                    IcehottMetrics.Tag("provider", provider),
                    IcehottMetrics.Tag("operation", operation));
            throw;
        }
    }

    private void Record(string operation, string outcome) =>
        IcehottMetrics.ArtifactStoreOperations.Add(
            1,
            IcehottMetrics.Tag("provider", provider),
            IcehottMetrics.Tag("operation", operation),
            IcehottMetrics.Tag("outcome", outcome));

    private static string Classify(Exception exception) => exception switch
    {
        OperationCanceledException => "cancelled",
        ArtifactStoreUnavailableException => "unavailable",
        ArtifactStoreIntegrityException => "integrity",
        ArtifactTooLargeException => "too_large",
        FileNotFoundException => "not_found",
        _ => "error"
    };
}
