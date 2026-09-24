namespace ICEHOTT.Domain.Knowledge;

public sealed class EmbeddingProfile
{
    private EmbeddingProfile() { }

    public EmbeddingProfile(
        Guid id,
        string key,
        string provider,
        string model,
        int dimensions,
        string version,
        int indexVersion,
        string distanceMetric,
        string normalization,
        EmbeddingProfileStatus status,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Profile ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Profile key is required.", nameof(key));
        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("Provider is required.", nameof(provider));
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.", nameof(model));
        if (dimensions <= 0 || dimensions > 16000)
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        if (indexVersion <= 0) throw new ArgumentOutOfRangeException(nameof(indexVersion));

        Id = id;
        Key = key.Trim();
        Provider = provider.Trim();
        Model = model.Trim();
        Dimensions = dimensions;
        Version = string.IsNullOrWhiteSpace(version) ? "1" : version.Trim();
        IndexVersion = indexVersion;
        DistanceMetric = string.IsNullOrWhiteSpace(distanceMetric) ? "cosine" : distanceMetric.Trim();
        Normalization = string.IsNullOrWhiteSpace(normalization) ? "unit" : normalization.Trim();
        Status = status;
        CreatedAtUtc = createdAtUtc;
        ActivatedAtUtc = status == EmbeddingProfileStatus.Active ? createdAtUtc : null;
    }

    public Guid Id { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Provider { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public int Dimensions { get; private set; }
    public string Version { get; private set; } = string.Empty;
    public int IndexVersion { get; private set; }
    public string DistanceMetric { get; private set; } = string.Empty;
    public string Normalization { get; private set; } = string.Empty;
    public EmbeddingProfileStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? ActivatedAtUtc { get; private set; }

    public void Activate(DateTimeOffset now)
    {
        if (Status != EmbeddingProfileStatus.Building)
            throw new InvalidOperationException(
                $"Only a Building embedding profile can be activated. Current status: {Status}.");

        Status = EmbeddingProfileStatus.Active;
        ActivatedAtUtc = now;
    }

    public void Retire()
    {
        if (Status != EmbeddingProfileStatus.Active)
            throw new InvalidOperationException(
                $"Only an Active embedding profile can be retired. Current status: {Status}.");

        Status = EmbeddingProfileStatus.Retired;
    }

    public void MarkFailed()
    {
        if (Status != EmbeddingProfileStatus.Building)
            throw new InvalidOperationException(
                $"Only a Building embedding profile can be marked failed. Current status: {Status}.");

        Status = EmbeddingProfileStatus.Failed;
    }
}
