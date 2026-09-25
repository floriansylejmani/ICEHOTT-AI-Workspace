namespace ICEHOTT.Application.Abstractions;

public sealed record EmbeddingBuildChunk(
    Guid ChunkId,
    Guid WorkspaceId,
    string Content);

public sealed record EmbeddingProfileCoverage(
    long ReadyChunkCount,
    long EmbeddedChunkCount)
{
    public long MissingChunkCount =>
        Math.Max(0, ReadyChunkCount - EmbeddedChunkCount);

    public bool IsComplete =>
        ReadyChunkCount == EmbeddedChunkCount;
}

public interface IEmbeddingBuildStore
{
    Task<IReadOnlyList<EmbeddingBuildChunk>> GetMissingReadyChunksAsync(
        Guid profileId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<EmbeddingProfileCoverage> GetCoverageAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
}
