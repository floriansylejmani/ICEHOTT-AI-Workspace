using System.Security.Cryptography;
using System.Text;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Infrastructure.Artifacts;
using Microsoft.Extensions.Options;

namespace ICEHOTT.Tests;

public sealed class LocalArtifactStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(
            Path.GetTempPath(),
            "icehott-artifact-tests",
            Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Stage_Commit_Read_Computes_Server_Checksum()
    {
        var store = CreateStore();
        var workspaceId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("artifact payload");

        await using var source = new MemoryStream(bytes, writable: false);
        var staged = await store.StageAsync(
            workspaceId,
            artifactId,
            source,
            1024);

        Assert.Equal(bytes.Length, staged.SizeBytes);
        Assert.Equal(Sha(bytes), staged.Sha256);
        Assert.StartsWith(
            $"staging/{workspaceId:N}/{artifactId:N}",
            staged.StagingKey,
            StringComparison.Ordinal);
        Assert.StartsWith(
            $"objects/{workspaceId:N}/{artifactId:N}",
            staged.StorageKey,
            StringComparison.Ordinal);

        await store.CommitAsync(
            staged.StagingKey,
            staged.StorageKey,
            staged.SizeBytes,
            staged.Sha256);

        var info = await store.GetInfoAsync(staged.StorageKey);
        Assert.NotNull(info);
        Assert.Equal(bytes.Length, info!.SizeBytes);
        Assert.Equal(Sha(bytes), info.Sha256);

        await using var content = await store.OpenReadAsync(staged.StorageKey);
        Assert.NotNull(content);

        using var copy = new MemoryStream();
        await content!.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Fact]
    public async Task Commit_Is_Idempotent_After_Staging_Move()
    {
        var store = CreateStore();
        var bytes = Encoding.UTF8.GetBytes("repeat-safe");

        await using var source = new MemoryStream(bytes, writable: false);
        var staged = await store.StageAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            source,
            1024);

        await store.CommitAsync(
            staged.StagingKey,
            staged.StorageKey,
            staged.SizeBytes,
            staged.Sha256);

        await store.CommitAsync(
            staged.StagingKey,
            staged.StorageKey,
            staged.SizeBytes,
            staged.Sha256);

        var info = await store.GetInfoAsync(staged.StorageKey);
        Assert.NotNull(info);
        Assert.Equal(Sha(bytes), info!.Sha256);
    }

    [Fact]
    public async Task Oversized_Stage_Is_Rejected_And_Cleans_Temporary_File()
    {
        var store = CreateStore();
        var workspaceId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var bytes = new byte[33];

        await using var source = new MemoryStream(bytes, writable: false);

        await Assert.ThrowsAsync<ArtifactTooLargeException>(
            () => store.StageAsync(
                workspaceId,
                artifactId,
                source,
                32));

        var stagingPath = Path.Combine(
            _root,
            "staging",
            workspaceId.ToString("N"),
            $"{artifactId:N}.stage");

        Assert.False(File.Exists(stagingPath));
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("objects/../../outside.bin")]
    [InlineData("objects/not-a-guid/file.bin")]
    [InlineData("other/11111111111111111111111111111111/22222222222222222222222222222222.bin")]
    public async Task Storage_Key_Traversal_Or_Invalid_Shape_Is_Rejected(
        string key)
    {
        var store = CreateStore();

        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(
            () => store.GetInfoAsync(key));
    }

    [Fact]
    public async Task Commit_Rejects_Mismatched_Staging_And_Final_Identity()
    {
        var store = CreateStore();
        var workspaceId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        await using var source = new MemoryStream(
            Encoding.UTF8.GetBytes("identity"),
            writable: false);

        var staged = await store.StageAsync(
            workspaceId,
            artifactId,
            source,
            1024);

        var wrongStorageKey =
            $"objects/{workspaceId:N}/{Guid.NewGuid():N}.bin";

        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(
            () => store.CommitAsync(
                staged.StagingKey,
                wrongStorageKey,
                staged.SizeBytes,
                staged.Sha256));
    }

    [Fact]
    public async Task Commit_Rejects_Checksum_Mismatch()
    {
        var store = CreateStore();

        await using var source = new MemoryStream(
            Encoding.UTF8.GetBytes("checksum"),
            writable: false);

        var staged = await store.StageAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            source,
            1024);

        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(
            () => store.CommitAsync(
                staged.StagingKey,
                staged.StorageKey,
                staged.SizeBytes,
                new string('0', 64)));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private LocalArtifactStore CreateStore() =>
        new(
            Options.Create(
                new ArtifactStorageOptions
                {
                    RootPath = _root
                }));

    private static string Sha(byte[] bytes) =>
        Convert.ToHexString(
                SHA256.HashData(bytes))
            .ToLowerInvariant();
}
