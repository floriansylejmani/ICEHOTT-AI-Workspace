namespace ICEHOTT.Application.Abstractions;

public sealed record EmbeddingProfileCoverage(
    long ExpectedReadyChunks,
    long EmbeddedReadyChunks)
{
    public bool IsComplete =>
        ExpectedReadyChunks == EmbeddedReadyChunks;
}

public interface IVectorIndexProvisioner
{
    Task EnsureBuildIndexAsync(
        EmbeddingProfileDescriptor profile,
        CancellationToken cancellationToken = default);

    Task<bool> IsIndexReadyAsync(
        EmbeddingProfileDescriptor profile,
        CancellationToken cancellationToken = default);

    Task DropRetiredIndexAsync(
        EmbeddingProfileDescriptor profile,
        CancellationToken cancellationToken = default);
}

public interface IEmbeddingProfileCoverageService
{
    Task<EmbeddingProfileCoverage> GetCoverageAsync(
        EmbeddingProfileDescriptor profile,
        CancellationToken cancellationToken = default);
}
