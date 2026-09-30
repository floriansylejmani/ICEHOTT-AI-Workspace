using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Infrastructure.Artifacts;
using Microsoft.Extensions.Options;

namespace ICEHOTT.Tests;

public sealed class S3ArtifactStoreIntegrationTests
{
    [Fact]
    public async Task Minio_Endpoint_Is_Reachable_And_Bucket_Is_Usable()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        var response = await context.Client.ListObjectsV2Async(
            new ListObjectsV2Request
            {
                BucketName = context.Bucket,
                Prefix = context.Prefix + "/",
                MaxKeys = 1
            });

        Assert.Equal(HttpStatusCode.OK, response.HttpStatusCode);
    }

    [Fact]
    public async Task Stage_Commit_Read_Info_And_Delete_Round_Trip()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        var workspaceId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("phase-7b-round-trip");
        await using var source = new MemoryStream(bytes);

        var staged = await context.Store.StageAsync(
            workspaceId,
            artifactId,
            source,
            1024);

        Assert.Equal(bytes.Length, staged.SizeBytes);
        Assert.Equal(Sha(bytes), staged.Sha256);

        await context.Store.CommitAsync(
            staged.StagingKey,
            staged.StorageKey,
            staged.SizeBytes,
            staged.Sha256);

        var info = await context.Store.GetInfoAsync(staged.StorageKey);
        Assert.NotNull(info);
        Assert.Equal(bytes.Length, info.SizeBytes);
        Assert.Equal(staged.Sha256, info.Sha256);

        await using var stream = await context.Store.OpenReadAsync(staged.StorageKey);
        Assert.NotNull(stream);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());

        await context.Store.DeleteAsync(staged.StorageKey);
        await context.Store.DeleteAsync(staged.StorageKey);
        Assert.Null(await context.Store.GetInfoAsync(staged.StorageKey));
    }

    [Fact]
    public async Task Empty_And_OverLimit_Content_Never_Create_Remote_Objects()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        var workspaceId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            context.Store.StageAsync(
                workspaceId,
                Guid.NewGuid(),
                new MemoryStream(),
                10));

        await Assert.ThrowsAsync<ArtifactTooLargeException>(() =>
            context.Store.StageAsync(
                workspaceId,
                Guid.NewGuid(),
                new MemoryStream(new byte[11]),
                10));

        Assert.Empty(await context.ListAllAsync());
    }

    [Fact]
    public async Task Commit_Is_Idempotent_And_Concurrent_Winner_Is_Verified()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        var workspaceId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("concurrent-commit");

        var staged = await context.StageAsync(workspaceId, artifactId, bytes);
        await Task.WhenAll(
            context.Store.CommitAsync(
                staged.StagingKey,
                staged.StorageKey,
                staged.SizeBytes,
                staged.Sha256),
            context.Store.CommitAsync(
                staged.StagingKey,
                staged.StorageKey,
                staged.SizeBytes,
                staged.Sha256));

        var info = await context.Store.GetInfoAsync(staged.StorageKey);
        Assert.NotNull(info);
        Assert.Equal(staged.Sha256, info.Sha256);

        var restaged = await context.StageAsync(workspaceId, artifactId, bytes);
        await context.Store.CommitAsync(
            restaged.StagingKey,
            restaged.StorageKey,
            restaged.SizeBytes,
            restaged.Sha256);

        Assert.Single(await context.ListObjectsAsync());
        Assert.Empty(await context.ListStagingAsync());
    }

    [Fact]
    public async Task Wrong_Expected_Hash_Fails_Closed_Without_Final_Object()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        var staged = await context.StageAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Encoding.UTF8.GetBytes("integrity-check"));

        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(() =>
            context.Store.CommitAsync(
                staged.StagingKey,
                staged.StorageKey,
                staged.SizeBytes,
                new string('0', 64)));

        Assert.Null(await context.Store.GetInfoAsync(staged.StorageKey));
        Assert.Single(await context.ListStagingAsync());
    }

    [Fact]
    public async Task Cleanup_Staging_Respects_Age_And_Max_Items()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        await context.StageAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Encoding.UTF8.GetBytes("staging-one"));
        await context.StageAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Encoding.UTF8.GetBytes("staging-two"));

        Assert.Equal(2, (await context.ListStagingAsync()).Count);

        var first = await context.Store.CleanupStagingAsync(
            DateTimeOffset.UtcNow.AddMinutes(1),
            1);

        Assert.Equal(1, first);
        Assert.Single(await context.ListStagingAsync());

        var second = await context.Store.CleanupStagingAsync(
            DateTimeOffset.UtcNow.AddMinutes(1),
            10);

        Assert.Equal(1, second);
        Assert.Empty(await context.ListStagingAsync());
    }

    [Fact]
    public async Task Prefixes_Isolate_Stores_In_The_Same_Bucket()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        var otherPrefix = context.Prefix + "-other";
        var otherStore = context.CreateStore(otherPrefix);

        var workspaceId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("same-logical-identity");

        var first = await context.StageAsync(workspaceId, artifactId, bytes);
        await context.Store.CommitAsync(
            first.StagingKey,
            first.StorageKey,
            first.SizeBytes,
            first.Sha256);

        var second = await S3TestContext.StageAsync(
            otherStore,
            workspaceId,
            artifactId,
            bytes);
        await otherStore.CommitAsync(
            second.StagingKey,
            second.StorageKey,
            second.SizeBytes,
            second.Sha256);

        await context.Store.DeleteAsync(first.StorageKey);
        Assert.Null(await context.Store.GetInfoAsync(first.StorageKey));
        Assert.NotNull(await otherStore.GetInfoAsync(second.StorageKey));

        await context.DeletePrefixAsync(otherPrefix);
    }

    [Fact]
    public async Task Missing_Objects_Are_Null_And_Delete_Is_Idempotent()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        var key = $"objects/{Guid.NewGuid():N}/{Guid.NewGuid():N}.bin";

        Assert.Null(await context.Store.GetInfoAsync(key));
        Assert.Null(await context.Store.OpenReadAsync(key));
        await context.Store.DeleteAsync(key);
        await context.Store.DeleteAsync(key);
    }

    [Fact]
    public async Task Max_Size_Boundary_Is_Exact_And_Oversize_Leaves_Nothing_Behind()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        var workspaceId = Guid.NewGuid();
        var exact = new byte[10];
        Random.Shared.NextBytes(exact);

        await using (var ok = new MemoryStream(exact))
        {
            var staged = await context.Store.StageAsync(workspaceId, Guid.NewGuid(), ok, 10);
            Assert.Equal(10, staged.SizeBytes);
        }

        Assert.Single(await context.ListAllAsync());

        await Assert.ThrowsAsync<ArtifactTooLargeException>(() =>
            context.Store.StageAsync(
                workspaceId,
                Guid.NewGuid(),
                new MemoryStream(new byte[11]),
                10));

        Assert.Single(await context.ListAllAsync());
    }

    [Fact]
    public async Task Large_Object_Streams_Through_Stage_Commit_And_Read_With_Exact_Hash()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        var bytes = new byte[5 * 1024 * 1024 + 123];
        Random.Shared.NextBytes(bytes);

        var staged = await context.StageAsync(Guid.NewGuid(), Guid.NewGuid(), bytes);
        Assert.Equal(bytes.Length, staged.SizeBytes);
        Assert.Equal(Sha(bytes), staged.Sha256);

        await context.Store.CommitAsync(
            staged.StagingKey,
            staged.StorageKey,
            staged.SizeBytes,
            staged.Sha256);

        var info = await context.Store.GetInfoAsync(staged.StorageKey);
        Assert.Equal(Sha(bytes), info!.Sha256);

        await using var stream = await context.Store.OpenReadAsync(staged.StorageKey);
        using var copy = new MemoryStream();
        await stream!.CopyToAsync(copy);
        Assert.Equal(Sha(bytes), Sha(copy.ToArray()));
    }

    [Fact]
    public async Task Staging_Never_Overwrites_An_Existing_Staged_Object()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        var workspaceId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var first = Encoding.UTF8.GetBytes("first-writer");

        var staged = await context.StageAsync(workspaceId, artifactId, first);

        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(() =>
            context.StageAsync(workspaceId, artifactId, Encoding.UTF8.GetBytes("second-writer")));

        await context.Store.CommitAsync(
            staged.StagingKey,
            staged.StorageKey,
            staged.SizeBytes,
            staged.Sha256);

        Assert.Equal(Sha(first), (await context.Store.GetInfoAsync(staged.StorageKey))!.Sha256);
    }

    [Fact]
    public async Task Existing_Correct_Final_Object_Is_Accepted_And_Staging_Removed()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        var workspaceId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("already-committed");

        var staged = await context.StageAsync(workspaceId, artifactId, bytes);
        await context.PutRawAsync(staged.StorageKey, bytes);

        await context.Store.CommitAsync(
            staged.StagingKey,
            staged.StorageKey,
            staged.SizeBytes,
            staged.Sha256);

        Assert.Empty(await context.ListStagingAsync());
        Assert.Equal(Sha(bytes), (await context.Store.GetInfoAsync(staged.StorageKey))!.Sha256);
    }

    [Fact]
    public async Task Existing_Incorrect_Final_Object_Is_Rejected_Without_Being_Overwritten()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        var workspaceId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var expected = Encoding.UTF8.GetBytes("expected-content");
        var foreign = Encoding.UTF8.GetBytes("foreign-content!");

        var staged = await context.StageAsync(workspaceId, artifactId, expected);
        await context.PutRawAsync(staged.StorageKey, foreign);

        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(() =>
            context.Store.CommitAsync(
                staged.StagingKey,
                staged.StorageKey,
                staged.SizeBytes,
                staged.Sha256));

        var info = await context.Store.GetInfoAsync(staged.StorageKey);
        Assert.Equal(Sha(foreign), info!.Sha256);
        Assert.Single(await context.ListStagingAsync());
    }

    [Fact]
    public async Task Commit_Rejects_Cross_Identity_Keys_And_Creates_Nothing()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        var workspaceId = Guid.NewGuid();
        var staged = await context.StageAsync(
            workspaceId,
            Guid.NewGuid(),
            Encoding.UTF8.GetBytes("identity"));

        var otherArtifactKey = $"objects/{workspaceId:N}/{Guid.NewGuid():N}.bin";
        var otherWorkspaceKey = $"objects/{Guid.NewGuid():N}/{Guid.NewGuid():N}.bin";

        foreach (var target in new[] { otherArtifactKey, otherWorkspaceKey })
        {
            await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(() =>
                context.Store.CommitAsync(
                    staged.StagingKey,
                    target,
                    staged.SizeBytes,
                    staged.Sha256));
        }

        Assert.Empty(await context.ListObjectsAsync());
        Assert.Single(await context.ListStagingAsync());
    }

    [Fact]
    public async Task Cleanup_Respects_Age_Threshold_And_Never_Touches_Final_Objects()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        var committed = await context.StageAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Encoding.UTF8.GetBytes("final-object"));
        await context.Store.CommitAsync(
            committed.StagingKey,
            committed.StorageKey,
            committed.SizeBytes,
            committed.Sha256);

        await context.StageAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Encoding.UTF8.GetBytes("fresh-staging"));

        // Threshold in the past: the freshly staged object is too young to delete.
        Assert.Equal(
            0,
            await context.Store.CleanupStagingAsync(DateTimeOffset.UtcNow.AddHours(-1), 100));
        Assert.Single(await context.ListStagingAsync());

        // Threshold in the future: staging is eligible, final objects still are not.
        Assert.Equal(
            1,
            await context.Store.CleanupStagingAsync(DateTimeOffset.UtcNow.AddHours(1), 100));
        Assert.Empty(await context.ListStagingAsync());
        Assert.Single(await context.ListObjectsAsync());
        Assert.NotNull(await context.Store.GetInfoAsync(committed.StorageKey));
    }

    [Fact]
    public async Task Cleanup_Cannot_Cross_Prefix_Boundaries()
    {
        await using var context = await S3TestContext.TryCreateAsync();
        if (context is null)
            return;

        var siblingPrefix = context.Prefix + "-sibling";
        // A store whose prefix nests inside this store's "staging/" namespace.
        var nestedPrefix = context.Prefix + "/staging";
        var sibling = context.CreateStore(siblingPrefix);
        var nested = context.CreateStore(nestedPrefix);

        try
        {
            var own = await context.StageAsync(
                Guid.NewGuid(), Guid.NewGuid(), Encoding.UTF8.GetBytes("own"));
            var siblingStaged = await S3TestContext.StageAsync(
                sibling, Guid.NewGuid(), Guid.NewGuid(), Encoding.UTF8.GetBytes("sibling"));
            var nestedStaged = await S3TestContext.StageAsync(
                nested, Guid.NewGuid(), Guid.NewGuid(), Encoding.UTF8.GetBytes("nested"));

            var deleted = await context.Store.CleanupStagingAsync(
                DateTimeOffset.UtcNow.AddHours(1),
                100);

            Assert.Equal(1, deleted);

            // Sibling and nested stores keep their staged objects.
            await sibling.CommitAsync(
                siblingStaged.StagingKey,
                siblingStaged.StorageKey,
                siblingStaged.SizeBytes,
                siblingStaged.Sha256);
            await nested.CommitAsync(
                nestedStaged.StagingKey,
                nestedStaged.StorageKey,
                nestedStaged.SizeBytes,
                nestedStaged.Sha256);

            Assert.NotNull(await sibling.GetInfoAsync(siblingStaged.StorageKey));
            Assert.NotNull(await nested.GetInfoAsync(nestedStaged.StorageKey));
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                context.Store.CommitAsync(
                    own.StagingKey,
                    own.StorageKey,
                    own.SizeBytes,
                    own.Sha256));
        }
        finally
        {
            await context.DeletePrefixAsync(siblingPrefix);
        }
    }

    private static string Sha(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed class S3TestContext : IAsyncDisposable
    {
        private S3TestContext(
            IAmazonS3 client,
            string bucket,
            string region,
            string endpoint,
            string accessKey,
            string secretKey,
            string prefix)
        {
            Client = client;
            Bucket = bucket;
            Region = region;
            Endpoint = endpoint;
            AccessKey = accessKey;
            SecretKey = secretKey;
            Prefix = prefix;
            Store = CreateStore(prefix);
        }

        public IAmazonS3 Client { get; }
        public string Bucket { get; }
        public string Region { get; }
        public string Endpoint { get; }
        public string AccessKey { get; }
        public string SecretKey { get; }
        public string Prefix { get; }
        public S3ArtifactStore Store { get; }

        public static async Task<S3TestContext?> TryCreateAsync()
        {
            var endpoint = Environment.GetEnvironmentVariable("ICEHOTT_S3_TEST_ENDPOINT");
            var bucket = Environment.GetEnvironmentVariable("ICEHOTT_S3_TEST_BUCKET");
            var access = Environment.GetEnvironmentVariable("ICEHOTT_S3_TEST_ACCESS_KEY");
            var secret = Environment.GetEnvironmentVariable("ICEHOTT_S3_TEST_SECRET_KEY");
            var region = Environment.GetEnvironmentVariable("ICEHOTT_S3_TEST_REGION")
                ?? "us-east-1";

            var missing = new[]
            {
                endpoint,
                bucket,
                access,
                secret
            }.Any(string.IsNullOrWhiteSpace);

            if (missing)
            {
                if (string.Equals(
                        Environment.GetEnvironmentVariable("CI"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                    Assert.Fail(
                        "CI must configure ICEHOTT_S3_TEST_ENDPOINT, BUCKET, ACCESS_KEY and SECRET_KEY.");

                return null;
            }

            var client = CreateClient(endpoint!, region, access!, secret!);
            try
            {
                // CI creates the bucket explicitly; only local runs may create it here.
                if (!string.Equals(
                        Environment.GetEnvironmentVariable("CI"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await client.PutBucketAsync(
                            new PutBucketRequest
                            {
                                BucketName = bucket!,
                                UseClientRegion = true
                            });
                    }
                    catch (AmazonS3Exception exception)
                        when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest)
                    {
                        // The local test bucket already exists.
                    }
                }

                var prefix = $"phase7b-tests/{Guid.NewGuid():N}";
                return new S3TestContext(
                    client,
                    bucket!,
                    region,
                    endpoint!,
                    access!,
                    secret!,
                    prefix);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public S3ArtifactStore CreateStore(string prefix) =>
            new(
                Client,
                Options.Create(
                    new ObjectStorageOptions
                    {
                        Endpoint = Endpoint,
                        Bucket = Bucket,
                        Region = Region,
                        AccessKeyId = AccessKey,
                        SecretAccessKey = SecretKey,
                        ForcePathStyle = true,
                        Prefix = prefix
                    }));

        public Task<ArtifactStageResult> StageAsync(
            Guid workspaceId,
            Guid artifactId,
            byte[] bytes) =>
            StageAsync(Store, workspaceId, artifactId, bytes);

        public static async Task<ArtifactStageResult> StageAsync(
            S3ArtifactStore store,
            Guid workspaceId,
            Guid artifactId,
            byte[] bytes)
        {
            await using var stream = new MemoryStream(bytes);
            return await store.StageAsync(
                workspaceId,
                artifactId,
                stream,
                Math.Max(bytes.Length, 1) + 1024);
        }

        public async Task PutRawAsync(string logicalKey, byte[] bytes)
        {
            await using var stream = new MemoryStream(bytes);
            await Client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = Bucket,
                    Key = $"{Prefix}/{logicalKey}",
                    InputStream = stream
                });
        }

        public Task<IReadOnlyList<S3Object>> ListAllAsync() =>
            ListPrefixAsync(Prefix + "/");

        public Task<IReadOnlyList<S3Object>> ListObjectsAsync() =>
            ListPrefixAsync(Prefix + "/objects/");

        public Task<IReadOnlyList<S3Object>> ListStagingAsync() =>
            ListPrefixAsync(Prefix + "/staging/");

        private async Task<IReadOnlyList<S3Object>> ListPrefixAsync(string prefix)
        {
            var results = new List<S3Object>();
            string? token = null;

            do
            {
                var response = await Client.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = Bucket,
                        Prefix = prefix,
                        ContinuationToken = token
                    });
                results.AddRange(response.S3Objects ?? []);
                token = response.IsTruncated == true
                    ? response.NextContinuationToken
                    : null;
            }
            while (!string.IsNullOrEmpty(token));

            return results;
        }

        public async Task DeletePrefixAsync(string prefix)
        {
            var items = await ListPrefixAsync(prefix + "/");
            foreach (var item in items)
            {
                await Client.DeleteObjectAsync(
                    new DeleteObjectRequest
                    {
                        BucketName = Bucket,
                        Key = item.Key
                    });
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await DeletePrefixAsync(Prefix);
            }
            finally
            {
                Client.Dispose();
            }
        }

        private static IAmazonS3 CreateClient(
            string endpoint,
            string region,
            string accessKey,
            string secretKey)
        {
            var credentials = new BasicAWSCredentials(accessKey, secretKey);
            return new AmazonS3Client(
                credentials,
                new AmazonS3Config
                {
                    ServiceURL = endpoint.TrimEnd('/'),
                    AuthenticationRegion = region,
                    ForcePathStyle = true
                });
        }
    }
}
