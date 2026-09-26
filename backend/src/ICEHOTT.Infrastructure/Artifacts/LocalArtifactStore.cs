using System.Buffers;
using System.Security.Cryptography;
using ICEHOTT.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace ICEHOTT.Infrastructure.Artifacts;

public sealed class LocalArtifactStore : IArtifactStore
{
    private const int BufferSize = 64 * 1024;

    private readonly string _rootPath;
    private readonly string _rootPrefix;

    public LocalArtifactStore(IOptions<ArtifactStorageOptions> options)
    {
        var configured = options.Value.RootPath?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Artifact storage root path is required.");

        _rootPath = Path.GetFullPath(
            Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(Directory.GetCurrentDirectory(), configured));

        _rootPrefix = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(Path.Combine(_rootPath, "staging"));
        Directory.CreateDirectory(Path.Combine(_rootPath, "objects"));
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
        if (!source.CanRead)
            throw new ArgumentException("Artifact source stream must be readable.", nameof(source));

        var stagingKey = $"staging/{workspaceId:N}/{artifactId:N}.stage";
        var storageKey = $"objects/{workspaceId:N}/{artifactId:N}.bin";
        var stagingPath = ResolveKey(stagingKey, "staging");

        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await source.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        cancellationToken);

                    if (read == 0)
                        break;

                    if (total > maxBytes - read)
                        throw new ArtifactTooLargeException();

                    await target.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    total += read;
                }

                await target.FlushAsync(cancellationToken);
                target.Flush(flushToDisk: true);
            }

            if (total == 0)
                throw new InvalidDataException("Artifact content is empty.");

            var sha256 = Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();

            return new(
                stagingKey,
                storageKey,
                total,
                sha256);
        }
        catch
        {
            TryDeleteFile(stagingPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    public async Task CommitAsync(
        string stagingKey,
        string storageKey,
        long expectedSizeBytes,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (expectedSizeBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedSizeBytes));

        var expectedHash = NormalizeSha(expectedSha256);
        var stagingIdentity = ParseKey(stagingKey, "staging", ".stage");
        var storageIdentity = ParseKey(storageKey, "objects", ".bin");
        if (stagingIdentity != storageIdentity)
            throw new ArtifactStoreIntegrityException(
                "Artifact staging and storage keys do not identify the same object.");

        var stagingPath = ResolveKey(stagingKey, "staging");
        var storagePath = ResolveKey(storageKey, "objects");

        if (File.Exists(storagePath))
        {
            var existing = await ReadInfoAsync(storagePath, cancellationToken);
            EnsureIntegrity(existing, expectedSizeBytes, expectedHash);

            if (File.Exists(stagingPath))
                TryDeleteFile(stagingPath);

            return;
        }

        if (!File.Exists(stagingPath))
            throw new FileNotFoundException(
                "Artifact staging object is missing.",
                stagingKey);

        var staged = await ReadInfoAsync(stagingPath, cancellationToken);
        EnsureIntegrity(staged, expectedSizeBytes, expectedHash);

        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);

        try
        {
            File.Move(stagingPath, storagePath);
        }
        catch (IOException) when (File.Exists(storagePath))
        {
            var existing = await ReadInfoAsync(storagePath, cancellationToken);
            EnsureIntegrity(existing, expectedSizeBytes, expectedHash);
            TryDeleteFile(stagingPath);
        }
    }

    public async Task<ArtifactStoredObjectInfo?> GetInfoAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveKey(storageKey, "objects");
        if (!File.Exists(path))
            return null;

        return await ReadInfoAsync(path, cancellationToken);
    }

    public Task<Stream?> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = ResolveKey(storageKey, "objects");
        if (!File.Exists(path))
            return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryDeleteFile(ResolveKey(storageKey, "objects"));
        return Task.CompletedTask;
    }

    public Task DeleteStagingAsync(
        string stagingKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryDeleteFile(ResolveKey(stagingKey, "staging"));
        return Task.CompletedTask;
    }
    public Task<int> CleanupStagingAsync(
        DateTimeOffset olderThanUtc,
        int maxItems,
        CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(maxItems, 1, 1000);
        var stagingRoot = Path.Combine(_rootPath, "staging");

        if (!Directory.Exists(stagingRoot))
            return Task.FromResult(0);

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(
                     stagingRoot,
                     "*.stage",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (deleted >= capped)
                break;

            var lastWriteUtc = new DateTimeOffset(
                File.GetLastWriteTimeUtc(file),
                TimeSpan.Zero);

            if (lastWriteUtc > olderThanUtc)
                continue;

            TryDeleteFile(file);
            deleted++;
        }

        return Task.FromResult(deleted);
    }

    private async Task<ArtifactStoredObjectInfo> ReadInfoAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("Artifact storage object is missing.", path);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken);

                if (read == 0)
                    break;

                hash.AppendData(buffer, 0, read);
            }

            return new(
                fileInfo.Length,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private string ResolveKey(
        string key,
        string expectedRoot)
    {
        var expectedExtension = expectedRoot switch
        {
            "staging" => ".stage",
            "objects" => ".bin",
            _ => throw new ArtifactStoreIntegrityException(
                "Artifact storage root is invalid.")
        };

        ParseKey(key, expectedRoot, expectedExtension);
        if (string.IsNullOrWhiteSpace(key) ||
            Path.IsPathRooted(key) ||
            key.Contains('\\'))
            throw new ArtifactStoreIntegrityException("Artifact storage key is invalid.");

        var segments = key.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length != 3 ||
            !string.Equals(
                segments[0],
                expectedRoot,
                StringComparison.Ordinal) ||
            segments.Any(segment =>
                segment is "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArtifactStoreIntegrityException("Artifact storage key is invalid.");

        var relative = Path.Combine(segments);
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, relative));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(_rootPrefix, comparison))
            throw new ArtifactStoreIntegrityException("Artifact storage key escaped the storage root.");

        return fullPath;
    }

    private static (Guid WorkspaceId, Guid ArtifactId) ParseKey(
        string key,
        string expectedRoot,
        string expectedExtension)
    {
        if (string.IsNullOrWhiteSpace(key) ||
            Path.IsPathRooted(key) ||
            key.Contains('\\'))
            throw new ArtifactStoreIntegrityException(
                "Artifact storage key is invalid.");

        var segments = key.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length != 3 ||
            !string.Equals(
                segments[0],
                expectedRoot,
                StringComparison.Ordinal))
            throw new ArtifactStoreIntegrityException(
                "Artifact storage key is invalid.");

        if (!Guid.TryParseExact(
                segments[1],
                "N",
                out var workspaceId))
            throw new ArtifactStoreIntegrityException(
                "Artifact workspace key is invalid.");

        var fileName = segments[2];
        if (!fileName.EndsWith(
                expectedExtension,
                StringComparison.Ordinal))
            throw new ArtifactStoreIntegrityException(
                "Artifact storage key extension is invalid.");

        var artifactPart = fileName[..^expectedExtension.Length];
        if (!Guid.TryParseExact(
                artifactPart,
                "N",
                out var artifactId))
            throw new ArtifactStoreIntegrityException(
                "Artifact object key is invalid.");

        return (workspaceId, artifactId);
    }

    private static string NormalizeSha(string value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;

        if (normalized.Length != 64 ||
            normalized.Any(character =>
                !Uri.IsHexDigit(character)))
            throw new ArtifactStoreIntegrityException("Artifact SHA-256 is invalid.");

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

    private static void TryDeleteFile(string path)
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
}
