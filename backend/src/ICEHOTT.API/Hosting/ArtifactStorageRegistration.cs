using Amazon.Runtime;
using Amazon.S3;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Infrastructure.Artifacts;
using Microsoft.Extensions.Options;

namespace ICEHOTT.API.Hosting;

public static class ArtifactStorageRegistration
{
    public static IServiceCollection AddArtifactStore(
        this IServiceCollection services,
        IConfiguration configuration,
        ArtifactStorageOptions artifactStorage)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(artifactStorage);

        var provider = artifactStorage.Provider?.Trim();

        if (string.Equals(
                provider,
                "Local",
                StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(artifactStorage.RootPath))
                throw new InvalidOperationException(
                    "ArtifactStorage:RootPath is required for the Local provider.");

            services.AddSingleton<LocalArtifactStore>();
            services.AddSingleton<IArtifactStore>(provider =>
                new InstrumentedArtifactStore(
                    provider.GetRequiredService<LocalArtifactStore>(),
                    "local"));
            return services;
        }

        if (!string.Equals(
                provider,
                "S3",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "ArtifactStorage:Provider must be either 'Local' or 'S3'.");

        var options = configuration
            .GetSection(ObjectStorageOptions.SectionName)
            .Get<ObjectStorageOptions>() ??
            new ObjectStorageOptions();

        ValidateS3Startup(options);

        services.AddSingleton<IAmazonS3>(_ =>
        {
            var clientConfig = new AmazonS3Config
            {
                ServiceURL = options.Endpoint.TrimEnd('/'),
                ForcePathStyle = options.ForcePathStyle,
                AuthenticationRegion = options.Region
            };

            var credentials = new BasicAWSCredentials(
                options.AccessKeyId,
                options.SecretAccessKey);

            return new AmazonS3Client(
                credentials,
                clientConfig);
        });

        services.AddSingleton<S3ArtifactStore>();
        services.AddSingleton<IArtifactStore>(provider =>
            new InstrumentedArtifactStore(
                provider.GetRequiredService<S3ArtifactStore>(),
                "s3"));
        return services;
    }

    private static void ValidateS3Startup(ObjectStorageOptions options)
    {
        if (!Uri.TryCreate(
                options.Endpoint,
                UriKind.Absolute,
                out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new InvalidOperationException(
                "ObjectStorage:Endpoint must be an absolute HTTP or HTTPS endpoint without embedded credentials.");

        if (!ObjectStorageOptions.IsValidBucketName(options.Bucket))
            throw new InvalidOperationException(
                "ObjectStorage:Bucket is invalid.");

        if (string.IsNullOrWhiteSpace(options.Region))
            throw new InvalidOperationException(
                "ObjectStorage:Region is required.");

        if (string.IsNullOrWhiteSpace(options.AccessKeyId))
            throw new InvalidOperationException(
                "ObjectStorage:AccessKeyId is required.");

        if (string.IsNullOrWhiteSpace(options.SecretAccessKey))
            throw new InvalidOperationException(
                "ObjectStorage:SecretAccessKey is required.");

        if (!ObjectStorageOptions.TryNormalizePrefix(
                options.Prefix,
                out _))
            throw new InvalidOperationException(
                "ObjectStorage:Prefix is invalid.");
    }
}
