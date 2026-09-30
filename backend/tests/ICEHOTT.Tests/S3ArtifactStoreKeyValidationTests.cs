using Amazon.Runtime;
using Amazon.S3;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Infrastructure.Artifacts;
using Microsoft.Extensions.Options;

namespace ICEHOTT.Tests;

public sealed class S3ArtifactStoreKeyValidationTests : IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly S3ArtifactStore _store;

    public S3ArtifactStoreKeyValidationTests()
    {
        _client = new AmazonS3Client(
            new BasicAWSCredentials("unit-access", "unit-secret"),
            new AmazonS3Config
            {
                ServiceURL = "http://127.0.0.1:1",
                AuthenticationRegion = "us-east-1",
                ForcePathStyle = true,
                Timeout = TimeSpan.FromMilliseconds(250)
            });

        _store = new S3ArtifactStore(
            _client,
            Options.Create(new ObjectStorageOptions
            {
                Bucket = "icehott-unit-tests",
                Prefix = "phase7b"
            }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/objects/11111111111111111111111111111111/22222222222222222222222222222222.bin")]
    [InlineData("objects//11111111111111111111111111111111/22222222222222222222222222222222.bin")]
    [InlineData("objects/../22222222222222222222222222222222.bin")]
    [InlineData("objects/11111111111111111111111111111111/22222222222222222222222222222222.bin/")]
    [InlineData("objects\\11111111111111111111111111111111\\22222222222222222222222222222222.bin")]
    [InlineData("objects/11111111-1111-1111-1111-111111111111/22222222222222222222222222222222.bin")]
    [InlineData("objects/11111111111111111111111111111111/22222222-2222-2222-2222-222222222222.bin")]
    [InlineData("staging/11111111111111111111111111111111/22222222222222222222222222222222.stage")]
    public async Task Object_Operations_Reject_NonCanonical_Keys_Before_Network(string key)
    {
        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(
            () => _store.GetInfoAsync(key));
        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(
            () => _store.OpenReadAsync(key));
        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(
            () => _store.DeleteAsync(key));
    }

    [Fact]
    public async Task Commit_Rejects_Cross_Artifact_Identity_Before_Network()
    {
        var workspace = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var staging = $"staging/{workspace:N}/{first:N}.stage";
        var storage = $"objects/{workspace:N}/{second:N}.bin";

        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(
            () => _store.CommitAsync(
                staging,
                storage,
                1,
                new string('a', 64)));
    }

    public void Dispose() => _client.Dispose();
}
