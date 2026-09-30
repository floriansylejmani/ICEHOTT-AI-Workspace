using System.Reflection;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Infrastructure.Artifacts;
using Microsoft.Extensions.Options;

namespace ICEHOTT.Tests;

/// <summary>
/// Provider-free tests of <see cref="S3ArtifactStore"/> safety logic using a
/// recording fake <see cref="IAmazonS3"/>. Every validation test also asserts that
/// the fake client was never contacted, so removing a guard fails the test.
/// </summary>
public sealed class S3ArtifactStoreUnitTests
{
    // Contain hex letters so upper-casing them produces a different string.
    private static readonly Guid Workspace = Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab");
    private static readonly Guid Artifact = Guid.Parse("bcdefabc-defa-bcde-fabc-defabcdefabc");
    private static readonly string StagingKey = $"staging/{Workspace:N}/{Artifact:N}.stage";
    private static readonly string StorageKey = $"objects/{Workspace:N}/{Artifact:N}.bin";
    private static readonly string ValidSha = new('a', 64);

    public static TheoryData<string> InvalidStorageKeys() => new()
    {
        "",
        "   ",
        $"/objects/{Workspace:N}/{Artifact:N}.bin",
        $"objects/{Workspace:N}/{Artifact:N}.bin/",
        $"objects//{Workspace:N}/{Artifact:N}.bin",
        $"objects/../{Workspace:N}/{Artifact:N}.bin",
        $"objects/./{Artifact:N}.bin",
        $"objects\\{Workspace:N}\\{Artifact:N}.bin",
        $" objects/{Workspace:N}/{Artifact:N}.bin",
        $"objects/{Workspace:N}/{Artifact:N}.bin\n",
        $"objects/{Workspace:D}/{Artifact:N}.bin",
        $"objects/{Workspace:N}/{Artifact:D}.bin",
        $"objects/{Workspace:N}/{Artifact:N}.BIN",
        $"objects/{Workspace:N}/{Artifact:N}.bin.bak",
        $"objects/{Workspace:N}/{Artifact:N}.stage",
        $"objects/{Workspace.ToString("N").ToUpperInvariant()}/{Artifact:N}.bin",
        $"objects/{Workspace:N}/{Artifact.ToString("N").ToUpperInvariant()}.bin",
        $"objects/not-a-guid/{Artifact:N}.bin",
        $"objects/{Workspace:N}/not-a-guid.bin",
        $"objects/{Workspace:N}/.bin",
        $"objects/{Workspace:N}",
        $"objects/{Workspace:N}/{Artifact:N}/extra.bin",
        // Wrong key type for an "objects" operation.
        StagingKey,
        $"other/{Workspace:N}/{Artifact:N}.bin"
    };

    public static TheoryData<string> InvalidStagingKeys() => new()
    {
        "",
        $"/staging/{Workspace:N}/{Artifact:N}.stage",
        $"staging/../{Workspace:N}/{Artifact:N}.stage",
        $"staging\\{Workspace:N}\\{Artifact:N}.stage",
        $"staging/{Workspace:N}/{Artifact:N}.bin",
        $"staging/{Workspace:D}/{Artifact:N}.stage",
        $"staging/{Workspace:N}/not-a-guid.stage",
        StorageKey
    };

    [Theory]
    [MemberData(nameof(InvalidStorageKeys))]
    public async Task Object_Operations_Reject_Invalid_Logical_Keys_Without_Contacting_Provider(
        string key)
    {
        var fake = new FakeS3();
        var store = CreateStore(fake);

        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(() => store.GetInfoAsync(key));
        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(() => store.OpenReadAsync(key));
        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(() => store.DeleteAsync(key));

        Assert.Empty(fake.Calls);
    }

    [Theory]
    [MemberData(nameof(InvalidStagingKeys))]
    public async Task Staging_Delete_Rejects_Invalid_Logical_Keys_Without_Contacting_Provider(
        string key)
    {
        var fake = new FakeS3();

        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(
            () => CreateStore(fake).DeleteStagingAsync(key));

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Commit_Rejects_Keys_That_Do_Not_Identify_The_Same_Artifact()
    {
        var fake = new FakeS3();
        var store = CreateStore(fake);

        var otherArtifact = $"objects/{Workspace:N}/{Guid.NewGuid():N}.bin";
        var otherWorkspace = $"objects/{Guid.NewGuid():N}/{Artifact:N}.bin";

        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(
            () => store.CommitAsync(StagingKey, otherArtifact, 1, ValidSha));
        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(
            () => store.CommitAsync(StagingKey, otherWorkspace, 1, ValidSha));
        // Swapped roles are also wrong key types.
        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(
            () => store.CommitAsync(StorageKey, StagingKey, 1, ValidSha));

        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Commit_Rejects_Invalid_Sha256(string sha)
    {
        var fake = new FakeS3();

        await Assert.ThrowsAsync<ArtifactStoreIntegrityException>(
            () => CreateStore(fake).CommitAsync(StagingKey, StorageKey, 1, sha));

        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Commit_Rejects_Non_Positive_Expected_Size(long size)
    {
        var fake = new FakeS3();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CreateStore(fake).CommitAsync(StagingKey, StorageKey, size, ValidSha));

        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Stage_Rejects_Non_Positive_MaxBytes(long maxBytes)
    {
        var fake = new FakeS3();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CreateStore(fake).StageAsync(
                Workspace,
                Artifact,
                new MemoryStream(new byte[] { 1 }),
                maxBytes));

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Stage_Rejects_Empty_Ids_And_Unreadable_Source()
    {
        var fake = new FakeS3();
        var store = CreateStore(fake);
        var readable = new MemoryStream(new byte[] { 1 });

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.StageAsync(Guid.Empty, Artifact, readable, 10));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.StageAsync(Workspace, Guid.Empty, readable, 10));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.StageAsync(Workspace, Artifact, new WriteOnlyStream(), 10));

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Stage_Zero_Bytes_And_Oversize_Never_Reach_The_Provider()
    {
        var fake = new FakeS3();
        var store = CreateStore(fake);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.StageAsync(Workspace, Artifact, new MemoryStream(), 10));
        await Assert.ThrowsAsync<ArtifactTooLargeException>(
            () => store.StageAsync(Workspace, Artifact, new MemoryStream(new byte[11]), 10));

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Stage_Returns_Unprefixed_Logical_Keys_And_Sends_Prefixed_Conditional_Put()
    {
        var fake = new FakeS3();
        var store = CreateStore(fake, "tenant/env");
        var bytes = new byte[] { 1, 2, 3 };

        var result = await store.StageAsync(Workspace, Artifact, new MemoryStream(bytes), 10);

        Assert.Equal(StagingKey, result.StagingKey);
        Assert.Equal(StorageKey, result.StorageKey);
        Assert.Equal(3, result.SizeBytes);

        var put = Assert.Single(fake.Requests.OfType<PutObjectRequest>());
        Assert.Equal($"tenant/env/{StagingKey}", put.Key);
        Assert.Equal("*", put.IfNoneMatch);
        Assert.Null(put.CannedACL);
    }

    [Fact]
    public async Task Prefix_Is_Applied_To_Remote_Keys_Only()
    {
        var fake = new FakeS3();
        var store = CreateStore(fake, "tenant/env");

        Assert.Null(await store.GetInfoAsync(StorageKey));
        await store.DeleteAsync(StorageKey);

        Assert.Equal(
            $"tenant/env/{StorageKey}",
            fake.Requests.OfType<GetObjectRequest>().Single().Key);
        Assert.Equal(
            $"tenant/env/{StorageKey}",
            fake.Requests.OfType<DeleteObjectRequest>().Single().Key);
    }

    [Fact]
    public async Task Provider_Failures_Become_Provider_Neutral_Unavailable_Exceptions()
    {
        const string secret = "Sentinel-Provider-Detail-42";

        Exception[] faults =
        [
            new AmazonS3Exception(secret) { StatusCode = System.Net.HttpStatusCode.InternalServerError },
            new AmazonS3Exception(secret) { StatusCode = System.Net.HttpStatusCode.Forbidden },
            new AmazonClientException(secret),
            new HttpRequestException(secret),
            new IOException(secret)
        ];

        foreach (var fault in faults)
        {
            var store = CreateStore(new FakeS3 { Fault = fault });

            var operations = new Func<Task>[]
            {
                () => store.GetInfoAsync(StorageKey),
                () => store.OpenReadAsync(StorageKey),
                () => store.DeleteAsync(StorageKey),
                () => store.DeleteStagingAsync(StagingKey),
                () => store.CleanupStagingAsync(DateTimeOffset.UtcNow, 10),
                () => store.CommitAsync(StagingKey, StorageKey, 1, ValidSha),
                () => store.StageAsync(Workspace, Artifact, new MemoryStream(new byte[] { 1 }), 10)
            };

            foreach (var operation in operations)
            {
                var exception = await Assert.ThrowsAsync<ArtifactStoreUnavailableException>(operation);
                Assert.DoesNotContain(secret, exception.Message);
            }
        }
    }

    [Fact]
    public async Task Cancellation_Propagates_And_Is_Never_Translated()
    {
        var fake = new FakeS3 { Fault = new OperationCanceledException() };
        var store = CreateStore(fake);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetInfoAsync(StorageKey));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenReadAsync(StorageKey));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync(StorageKey));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CleanupStagingAsync(DateTimeOffset.UtcNow, 10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CommitAsync(StagingKey, StorageKey, 1, ValidSha));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.StageAsync(Workspace, Artifact, new MemoryStream(new byte[] { 1 }), 10));
    }

    [Fact]
    public async Task Missing_Objects_Are_Null_And_Delete_Is_Idempotent()
    {
        var fake = new FakeS3
        {
            Fault = new AmazonS3Exception("gone")
            {
                StatusCode = System.Net.HttpStatusCode.NotFound,
                ErrorCode = "NoSuchKey"
            }
        };
        var store = CreateStore(fake);

        Assert.Null(await store.GetInfoAsync(StorageKey));
        Assert.Null(await store.OpenReadAsync(StorageKey));
        await store.DeleteAsync(StorageKey);
        await store.DeleteStagingAsync(StagingKey);
    }

    [Fact]
    public async Task Cleanup_Only_Deletes_Old_Staging_Objects_Inside_The_Configured_Prefix()
    {
        var old = DateTime.UtcNow.AddHours(-2);
        var fresh = DateTime.UtcNow;
        var oldStaging = $"staging/{Guid.NewGuid():N}/{Guid.NewGuid():N}.stage";
        var freshStaging = $"staging/{Guid.NewGuid():N}/{Guid.NewGuid():N}.stage";

        var fake = new FakeS3
        {
            Listing =
            [
                new S3Object { Key = "tenant/" + oldStaging, LastModified = old },
                new S3Object { Key = "tenant/" + freshStaging, LastModified = fresh },
                // A different store's nested namespace and a final object must never be deleted.
                new S3Object { Key = $"tenant/staging/staging/{Guid.NewGuid():N}/{Guid.NewGuid():N}.stage", LastModified = old },
                new S3Object { Key = $"tenant/{StorageKey}", LastModified = old },
                new S3Object { Key = "tenant/staging/not-a-key", LastModified = old },
                new S3Object { Key = "other-tenant/" + oldStaging, LastModified = old }
            ]
        };

        var deleted = await CreateStore(fake, "tenant")
            .CleanupStagingAsync(DateTimeOffset.UtcNow.AddHours(-1), 100);

        Assert.Equal(1, deleted);
        Assert.Equal(
            "tenant/staging/",
            fake.Requests.OfType<ListObjectsV2Request>().First().Prefix);
        Assert.Equal(
            ["tenant/" + oldStaging],
            fake.Requests.OfType<DeleteObjectRequest>().Select(x => x.Key).ToArray());
    }

    [Fact]
    public async Task Cleanup_Handles_Provider_Returning_Null_Listing()
    {
        // AWSSDK v4 returns null (not empty) S3Objects for an empty prefix.
        var fake = new FakeS3 { NullListing = true };

        Assert.Equal(0, await CreateStore(fake).CleanupStagingAsync(DateTimeOffset.UtcNow, 10));
    }

    [Fact]
    public async Task Cleanup_Obeys_MaxItems()
    {
        var old = DateTime.UtcNow.AddHours(-2);
        var fake = new FakeS3
        {
            Listing = Enumerable.Range(0, 5)
                .Select(_ => new S3Object
                {
                    Key = $"staging/{Guid.NewGuid():N}/{Guid.NewGuid():N}.stage",
                    LastModified = old
                })
                .ToList()
        };

        var deleted = await CreateStore(fake).CleanupStagingAsync(DateTimeOffset.UtcNow, 2);

        Assert.Equal(2, deleted);
        Assert.Equal(2, fake.Requests.OfType<DeleteObjectRequest>().Count());
    }

    [Fact]
    public void Store_Rejects_Invalid_Bucket_Or_Prefix_At_Construction()
    {
        var fake = new FakeS3();

        Assert.Throws<InvalidOperationException>(() => new S3ArtifactStore(
            fake.Client,
            Options.Create(new ObjectStorageOptions { Bucket = "Bad_Bucket" })));
        Assert.Throws<InvalidOperationException>(() => new S3ArtifactStore(
            fake.Client,
            Options.Create(new ObjectStorageOptions { Bucket = "good-bucket", Prefix = "../x" })));
    }

    private static S3ArtifactStore CreateStore(FakeS3 fake, string prefix = "") =>
        new(
            fake.Client,
            Options.Create(
                new ObjectStorageOptions
                {
                    Endpoint = "https://objects.example",
                    Bucket = "unit-test-bucket",
                    AccessKeyId = "unit",
                    SecretAccessKey = "unit",
                    Prefix = prefix
                }));

    private sealed class WriteOnlyStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    /// <summary>Recording fake of the S3 client built on <see cref="DispatchProxy"/>.</summary>
    private sealed class FakeS3
    {
        public FakeS3()
        {
            Client = DispatchProxy.Create<IAmazonS3, Proxy>();
            ((Proxy)(object)Client).Owner = this;
        }

        public IAmazonS3 Client { get; }
        public List<string> Calls { get; } = [];
        public List<object> Requests { get; } = [];
        public Exception? Fault { get; set; }
        public List<S3Object> Listing { get; set; } = [];
        public bool NullListing { get; set; }

        private object? Handle(MethodInfo method, object?[] args)
        {
            Calls.Add(method.Name);
            if (args.Length > 0 && args[0] is not null && args[0] is not CancellationToken)
                Requests.Add(args[0]!);

            if (Fault is not null)
                throw Fault;

            return method.Name switch
            {
                nameof(IAmazonS3.ListObjectsV2Async) => new ListObjectsV2Response
                {
                    S3Objects = NullListing ? null : Listing,
                    IsTruncated = false
                },
                nameof(IAmazonS3.DeleteObjectAsync) => new DeleteObjectResponse(),
                nameof(IAmazonS3.GetObjectAsync) => throw new AmazonS3Exception("missing")
                {
                    StatusCode = System.Net.HttpStatusCode.NotFound,
                    ErrorCode = "NoSuchKey"
                },
                nameof(IAmazonS3.PutObjectAsync) => new PutObjectResponse(),
                _ => throw new NotSupportedException(method.Name)
            };
        }

        public class Proxy : DispatchProxy
        {
            public FakeS3 Owner { get; set; } = null!;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                var method = targetMethod!;
                var returnType = method.ReturnType;
                if (!returnType.IsGenericType || returnType.GetGenericTypeDefinition() != typeof(Task<>))
                    return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;

                var resultType = returnType.GetGenericArguments()[0];
                try
                {
                    var result = Owner.Handle(method, args ?? []);
                    return typeof(Task)
                        .GetMethod(nameof(Task.FromResult))!
                        .MakeGenericMethod(resultType)
                        .Invoke(null, [result]);
                }
                catch (Exception exception)
                {
                    return typeof(Task)
                        .GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
                        .MakeGenericMethod(resultType)
                        .Invoke(null, [exception]);
                }
            }
        }
    }
}
