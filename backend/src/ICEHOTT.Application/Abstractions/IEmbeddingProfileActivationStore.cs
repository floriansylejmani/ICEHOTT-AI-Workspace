namespace ICEHOTT.Application.Abstractions;

public sealed record EmbeddingProfileActivationResult(
    Guid PreviousActiveProfileId,
    Guid ActiveProfileId,
    DateTimeOffset ActivatedAtUtc);

public interface IEmbeddingProfileActivationStore
{
    Task<EmbeddingProfileActivationResult> ActivateAsync(
        Guid candidateProfileId,
        Guid deterministicEvidenceId,
        Guid? offlineEvidenceId,
        DateTimeOffset activatedAtUtc,
        CancellationToken cancellationToken = default);
}
