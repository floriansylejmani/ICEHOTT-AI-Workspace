using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ICEHOTT.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace ICEHOTT.Infrastructure.Artifacts;

public sealed class S3ArtifactStore : IArtifactStore
{
    private const int BufferSize = 64 * 1024;

    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _prefix;

    public S3ArtifactStore(
        IAmazonS3 client,
        IOptions<ObjectStorageOptions> options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(options);

        var configured = options.Value;
        if (!ObjectStorageOptions.IsValidBucketName(configured.Bucket))
            throw new InvalidOperationException("ObjectStorage:Bucket is invalid.");

        if (!ObjectStorageOptions.TryNormalizePrefix(
                configured.Prefix,
                out var prefix))
            throw new InvalidOperationException("ObjectStorage:Prefix is invalid.");

        _bucket = configured.Bucket.Trim();
        _prefix = prefix;
    }

    public async Task<ArtifactStageResult> StageAsync(
        Guid workspaceId,
        Guid artifactId,
        Stream source,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (artifactId == Guid.Empty)
            throw new ArgumentException("Artifact ID is required.", nameof(artifactId));
        if (maxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (source is null || !source.CanRead)
            throw new ArgumentException("Artifact source stream must be readable.", nameof(source));

        var stagingKey = $"staging/{workspaceId:N}/{artifactId:N}.stage";
        var storageKey = $"objects/{workspaceId:N}/{artifactId:N}.bin";
        var remoteStagingKey = RemoteKey(stagingKey);
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"icehott-artifact-{Guid.NewGuid():N}.tmp");

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;

        try
        {
            string sha256;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var temp = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                while (true)
                {
                    var read = await source.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        cancellationToken);
                    if (read == 0)
                        break;

                    if (total > maxBytes - read)
                        throw new ArtifactTooLargeException();

                    await temp.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    total += read;
                }

                await temp.FlushAsync(cancellationToken);

                if (total == 0)
                    throw new InvalidDataException("Artifact content is empty.");

                sha256 = Convert.ToHexString(hash.GetHashAndReset())
                    .ToLowerInvariant();
            }

            try
            {
                await using var upload = new FileStream(
                    tempPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                await _client.PutObjectAsync(
                    new PutObjectRequest
                    {
                        BucketName = _bucket,
                        Key = remoteStagingKey,
                        InputStream = upload,
                        AutoCloseStream = false,
                        AutoResetStreamPosition = false,
                        IfNoneMatch = "*"
                    },
                    cancellationToken);
            }
            catch (AmazonS3Exception exception) when (IsConditionalConflict(exception))
            {
                throw new ArtifactStoreIntegrityException(
                    "Artifact staging object already exists.");
            }
            catch (AmazonS3Exception exception)
            {
                await TryDeleteRemoteKeyAsync(remoteStagingKey);
                throw Unavailable(exception);
            }
            catch (Exception exception) when (IsTransport(exception))
            {
                await TryDeleteRemoteKeyAsync(remoteStagingKey);
                throw Unavailable(exception);
            }

            return new(
                stagingKey,
                storageKey,
                total,
                sha256);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            TryDeleteTempFile(tempPath);
        }
    }

    public async Task CommitAsync(
        string stagingKey,
        string storageKey,
        long expectedSizeBytes,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (expectedSizeBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedSizeBytes));

        var expectedHash = NormalizeSha(expectedSha256);
        var stagingIdentity = ParseKey(stagingKey, "staging", ".stage");
        var storageIdentity = ParseKey(storageKey, "objects", ".bin");

        if (stagingIdentity != storageIdentity)
            throw new ArtifactStoreIntegrityException(
                "Artifact staging and storage keys do not identify the same object.");

        var existing = await GetInfoAsync(storageKey, cancellationToken);
        if (existing is not null)
        {
            EnsureIntegrity(existing, expectedSizeBytes, expectedHash);
            await DeleteStagingAsync(stagingKey, cancellationToken);
            return;
        }

        var staged = await GetInfoForLogicalKeyAsync(
            stagingKey,
            "staging",
            cancellationToken);

        if (staged is null)
            throw new FileNotFoundException(
                "Artifact staging object is missing.",
                stagingKey);

        EnsureIntegrity(staged, expectedSizeBytes, expectedHash);

        try
        {
            await _client.CopyObjectAsync(
                new CopyObjectRequest
                {
                    SourceBucket = _bucket,
                    SourceKey = RemoteKey(stagingKey),
                    DestinationBucket = _bucket,
                    DestinationKey = RemoteKey(storageKey),
                    IfNoneMatch = "*"
                },
                cancellationToken);
        }
        catch (AmazonS3Exception exception) when (IsConditionalConflict(exception))
        {
            var winner = await GetInfoAsync(storageKey, cancellationToken);
            if (winner is null)
                throw Unavailable(exception);

            EnsureIntegrity(winner, expectedSizeBytes, expectedHash);
        }
        catch (AmazonS3Exception exception)
        {
            throw Unavailable(exception);
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            throw Unavailable(exception);
        }

        var committed = await GetInfoAsync(storageKey, cancellationToken)
            ?? throw new ArtifactStoreIntegrityException(
                "Artifact committed object is missing.");

        EnsureIntegrity(committed, expectedSizeBytes, expectedHash);
        await DeleteStagingAsync(stagingKey, cancellationToken);
    }

    public Task<ArtifactStoredObjectInfo?> GetInfoAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        ParseKey(storageKey, "objects", ".bin");
        return GetInfoForLogicalKeyAsync(
            storageKey,
            "objects",
            cancellationToken);
    }

    public async Task<Stream?> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        ParseKey(storageKey, "objects", ".bin");

        try
        {
            var response = await _client.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = _bucket,
                    Key = RemoteKey(storageKey)
                },
                cancellationToken);

            return new ResponseOwningStream(response);
        }
        catch (AmazonS3Exception exception) when (IsMissing(exception))
        {
            return null;
        }
        catch (AmazonS3Exception exception)
        {
            throw Unavailable(exception);
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            throw Unavailable(exception);
        }
    }

    public async Task DeleteAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        ParseKey(storageKey, "objects", ".bin");
        await DeleteRemoteKeyAsync(RemoteKey(storageKey), cancellationToken);
    }

    public async Task DeleteStagingAsync(
        string stagingKey,
        CancellationToken cancellationToken = default)
    {
        ParseKey(stagingKey, "staging", ".stage");
        await DeleteRemoteKeyAsync(RemoteKey(stagingKey), cancellationToken);
    }

    public async Task<int> CleanupStagingAsync(
        DateTimeOffset olderThanUtc,
        int maxItems,
        CancellationToken cancellationToken = default)
    {
        var cap = Math.Clamp(maxItems, 1, 1000);
        var deleted = 0;
        string? continuationToken = null;
        var remotePrefix = RemotePrefix("staging/");

        do
        {
            ListObjectsV2Response response;
            try
            {
                response = await _client.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = _bucket,
                        Prefix = remotePrefix,
                        ContinuationToken = continuationToken,
                        MaxKeys = 1000
                    },
                    cancellationToken);
            }
            catch (AmazonS3Exception exception)
            {
                throw Unavailable(exception);
            }
            catch (Exception exception) when (IsTransport(exception))
            {
                throw Unavailable(exception);
            }

            foreach (var item in response.S3Objects ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (deleted >= cap)
                    break;

                if (item.LastModified is not { } lastModifiedUtc)
                    continue;

                var lastModified = new DateTimeOffset(
                    DateTime.SpecifyKind(lastModifiedUtc, DateTimeKind.Utc));

                if (lastModified > olderThanUtc)
                    continue;

                if (!TryLogicalKey(item.Key, out var logicalKey))
                    continue;

                try
                {
                    ParseKey(logicalKey, "staging", ".stage");
                }
                catch (ArtifactStoreIntegrityException)
                {
                    continue;
                }

                await DeleteRemoteKeyAsync(item.Key, cancellationToken);
                deleted++;
            }

            continuationToken = response.IsTruncated == true
                ? response.NextContinuationToken
                : null;
        }
        while (deleted < cap && !string.IsNullOrEmpty(continuationToken));

        return deleted;
    }

    private async Task<ArtifactStoredObjectInfo?> GetInfoForLogicalKeyAsync(
        string logicalKey,
        string expectedRoot,
        CancellationToken cancellationToken)
    {
        ParseKey(
            logicalKey,
            expectedRoot,
            expectedRoot == "staging" ? ".stage" : ".bin");

        try
        {
            using var response = await _client.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = _bucket,
                    Key = RemoteKey(logicalKey)
                },
                cancellationToken);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            long total = 0;

            try
            {
                while (true)
                {
                    var read = await response.ResponseStream.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        cancellationToken);
                    if (read == 0)
                        break;

                    hash.AppendData(buffer, 0, read);
                    total += read;
                }

                return new ArtifactStoredObjectInfo(
                    total,
                    Convert.ToHexString(hash.GetHashAndReset())
                        .ToLowerInvariant());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (AmazonS3Exception exception) when (IsMissing(exception))
        {
            return null;
        }
        catch (AmazonS3Exception exception)
        {
            throw Unavailable(exception);
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            throw Unavailable(exception);
        }
    }

    private async Task DeleteRemoteKeyAsync(
        string remoteKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteObjectAsync(
                new DeleteObjectRequest
                {
                    BucketName = _bucket,
                    Key = remoteKey
                },
                cancellationToken);
        }
        catch (AmazonS3Exception exception) when (IsMissing(exception))
        {
        }
        catch (AmazonS3Exception exception)
        {
            throw Unavailable(exception);
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            throw Unavailable(exception);
        }
    }

    private async Task TryDeleteRemoteKeyAsync(string remoteKey)
    {
        // Best-effort rollback of a partial staging object. It must still run
        // when the caller was cancelled, so it uses its own bounded token.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await DeleteRemoteKeyAsync(remoteKey, timeout.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ArtifactStoreUnavailableException)
        {
        }
    }

    private string RemoteKey(string logicalKey) =>
        string.IsNullOrEmpty(_prefix)
            ? logicalKey
            : $"{_prefix}/{logicalKey}";

    private string RemotePrefix(string logicalPrefix) =>
        string.IsNullOrEmpty(_prefix)
            ? logicalPrefix
            : $"{_prefix}/{logicalPrefix}";

    private bool TryLogicalKey(string remoteKey, out string logicalKey)
    {
        logicalKey = string.Empty;

        if (string.IsNullOrEmpty(_prefix))
        {
            logicalKey = remoteKey;
            return true;
        }

        var prefix = _prefix + "/";
        if (!remoteKey.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        logicalKey = remoteKey[prefix.Length..];
        return true;
    }

    private static (Guid WorkspaceId, Guid ArtifactId) ParseKey(
        string key,
        string expectedRoot,
        string expectedExtension)
    {
        if (string.IsNullOrWhiteSpace(key) ||
            Path.IsPathRooted(key) ||
            key.StartsWith('/') ||
            key.Contains('\\') ||
            key.Any(char.IsControl))
            throw new ArtifactStoreIntegrityException(
                "Artifact storage key is invalid.");

        var segments = key.Split('/');

        if (segments.Length != 3 ||
            !string.Equals(segments[0], expectedRoot, StringComparison.Ordinal) ||
            segments.Any(segment => segment is "." or ".."))
            throw new ArtifactStoreIntegrityException(
                "Artifact storage key is invalid.");

        if (!Guid.TryParseExact(segments[1], "N", out var workspaceId) ||
            !string.Equals(workspaceId.ToString("N"), segments[1], StringComparison.Ordinal))
            throw new ArtifactStoreIntegrityException(
                "Artifact workspace key is invalid.");

        var fileName = segments[2];
        if (!fileName.EndsWith(expectedExtension, StringComparison.Ordinal))
            throw new ArtifactStoreIntegrityException(
                "Artifact storage key extension is invalid.");

        var artifactPart = fileName[..^expectedExtension.Length];
        if (!Guid.TryParseExact(artifactPart, "N", out var artifactId) ||
            !string.Equals(artifactId.ToString("N"), artifactPart, StringComparison.Ordinal))
            throw new ArtifactStoreIntegrityException(
                "Artifact object key is invalid.");

        return (workspaceId, artifactId);
    }

    private static string NormalizeSha(string value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;

        if (normalized.Length != 64 ||
            normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ArtifactStoreIntegrityException(
                "Artifact SHA-256 is invalid.");

        return normalized;
    }

    private static void EnsureIntegrity(
        ArtifactStoredObjectInfo actual,
        long expectedSize,
        string expectedSha)
    {
        if (actual.SizeBytes != expectedSize ||
            !string.Equals(
                actual.Sha256,
                expectedSha,
                StringComparison.OrdinalIgnoreCase))
            throw new ArtifactStoreIntegrityException(
                "Artifact storage object failed integrity verification.");
    }

    // Connectivity, timeout and protocol failures that are not S3 service errors.
    // Cancellation is deliberately excluded so it always propagates.
    private static bool IsTransport(Exception exception) =>
        exception is not OperationCanceledException and
            (AmazonClientException or HttpRequestException or IOException);

    private static bool IsMissing(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound ||
        string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal) ||
        string.Equals(exception.ErrorCode, "NotFound", StringComparison.Ordinal);

    private static bool IsConditionalConflict(AmazonS3Exception exception) =>
        exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed;

    private static ArtifactStoreUnavailableException Unavailable(Exception exception) =>
        new("Artifact object storage is unavailable.", exception);

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ResponseOwningStream(GetObjectResponse response) : Stream
    {
        private readonly GetObjectResponse _response = response;
        private Stream Inner => _response.ResponseStream;

        public override bool CanRead => Inner.CanRead;
        public override bool CanSeek => Inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => Inner.Length;

        public override long Position
        {
            get => Inner.Position;
            set => Inner.Position = value;
        }

        public override void Flush() => Inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            Inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            Inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Inner.FlushAsync(cancellationToken);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Inner.ReadAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _response.Dispose();

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Inner is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();

            _response.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
