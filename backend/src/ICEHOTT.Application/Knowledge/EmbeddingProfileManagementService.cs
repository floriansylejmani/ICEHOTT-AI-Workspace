using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

public sealed record CreateEmbeddingProfileCommand(
    Guid Id,
    string Key,
    string Provider,
    string Model,
    int Dimensions,
    string Version,
    int IndexVersion,
    string DistanceMetric = "cosine",
    string Normalization = "unit");

public sealed class EmbeddingProfileManagementService(
    IEmbeddingProfileRepository profiles,
    IEmbeddingProviderRegistry providerRegistry,
    IVectorIndexProvisioner indexProvisioner,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<EmbeddingProfileDescriptor> CreateBuildingAsync(
        CreateEmbeddingProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        if (await profiles.GetBuildingAsync(cancellationToken) is not null)
            throw new InvalidOperationException(
                "A Building embedding profile already exists.");

        var descriptor = new EmbeddingProfileDescriptor(
            command.Id,
            command.Key,
            command.Provider,
            command.Model,
            command.Dimensions,
            command.Version,
            command.IndexVersion,
            command.DistanceMetric,
            command.Normalization);

        descriptor.Validate();

        if (descriptor.Dimensions > 2000)
            throw new InvalidOperationException(
                "The current float32 HNSW path supports at most 2000 dimensions.");

        var provider = providerRegistry.Resolve(descriptor.Provider);
        ProfileKnowledgeRetriever.ValidateProviderCompatibility(
            provider,
            descriptor);

        var profile = new EmbeddingProfile(
            descriptor.Id,
            descriptor.Key,
            descriptor.Provider,
            descriptor.Model,
            descriptor.Dimensions,
            descriptor.Version,
            descriptor.IndexVersion,
            descriptor.DistanceMetric,
            descriptor.Normalization,
            EmbeddingProfileStatus.Building,
            clock.GetUtcNow());

        await profiles.AddAsync(profile, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        try
        {
            await indexProvisioner.EnsureBuildIndexAsync(
                descriptor,
                cancellationToken);
            return descriptor;
        }
        catch
        {
            profile.MarkFailed();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw;
        }
    }
}
