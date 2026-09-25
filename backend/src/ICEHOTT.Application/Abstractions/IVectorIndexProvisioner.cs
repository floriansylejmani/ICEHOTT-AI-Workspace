namespace ICEHOTT.Application.Abstractions;

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
