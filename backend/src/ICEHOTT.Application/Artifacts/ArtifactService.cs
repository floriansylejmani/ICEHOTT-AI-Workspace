using System.Net.Http.Headers;
using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Application.Artifacts;

public sealed class ArtifactService(
    IWorkspaceRepository workspaces,
    IArtifactRepository artifacts,
    IArtifactStore store,
    IWorkflowAuditRepository audit,
    IUnitOfWork unitOfWork,
    ArtifactPolicy policy,
    TimeProvider clock)
{
    public const int MaxFileNameLength = 260;
    public const int MaxContentTypeLength = 160;
    public const int MaxIdempotencyKeyLength = 160;

    public async Task<ArtifactResult<IReadOnlyList<ArtifactView>>> ListAsync(
        Guid userId,
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(
                userId,
                workspaceId,
                cancellationToken) is null)
            return new(null, "workspace_not_found");

        var items = await artifacts.ListAsync(
            workspaceId,
            Math.Clamp(limit, 1, 200),
            cancellationToken);

        return new(items.Select(Map).ToArray(), null);
    }

    public async Task<ArtifactResult<ArtifactView>> GetAsync(
        Guid userId,
        Guid workspaceId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(
                userId,
                workspaceId,
                cancellationToken) is null)
            return new(null, "workspace_not_found");

        var artifact = await artifacts.FindAsync(
            workspaceId,
            artifactId,
            cancellationToken);

        if (artifact is null || artifact.Status == ArtifactStatus.Deleted)
            return new(null, "artifact_not_found");

        return new(Map(artifact), null);
    }

    public async Task<ArtifactResult<ArtifactView>> UploadAsync(
        Guid userId,
        Guid workspaceId,
        string fileName,
        string? contentType,
        long? declaredLength,
        Stream source,
        string? idempotencyKey,
        Guid? workflowRunId = null,
        Guid? stepRunId = null,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(
                userId,
                workspaceId,
                cancellationToken) is null)
            return new(null, "workspace_not_found");

        var normalizedFileName = NormalizeFileName(fileName);
        if (normalizedFileName is null)
            return new(null, "file_name_required");
        if (normalizedFileName.Length > MaxFileNameLength)
            return new(null, "file_name_too_long");

        var normalizedContentType = NormalizeContentType(contentType);
        if (normalizedContentType is null)
            return new(null, "invalid_content_type");

        var normalizedIdempotency = NormalizeIdempotencyKey(idempotencyKey);
        if (idempotencyKey is not null && normalizedIdempotency is null)
            return new(null, "invalid_idempotency_key");

        if (declaredLength is <= 0)
            return new(null, "file_required");
        if (declaredLength > policy.MaxArtifactBytes)
            return new(null, "file_too_large");

        if (!source.CanRead)
            return new(null, "file_required");

        var artifactId = Guid.NewGuid();
        ArtifactStageResult staged;

        try
        {
            staged = await store.StageAsync(
                workspaceId,
                artifactId,
                source,
                policy.MaxArtifactBytes,
                cancellationToken);
        }
        catch (ArtifactTooLargeException)
        {
            return new(null, "file_too_large");
        }
        catch (InvalidDataException)
        {
            return new(null, "file_required");
        }
        catch (ArtifactStoreIntegrityException)
        {
            return new(null, "artifact_storage_failed");
        }
        catch (IOException)
        {
            return new(null, "artifact_storage_unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            return new(null, "artifact_storage_unavailable");
        }

        if (declaredLength is { } declared &&
            declared != staged.SizeBytes)
        {
            await SafeDeleteStagingAsync(
                staged.StagingKey,
                cancellationToken);
            return new(null, "file_length_mismatch");
        }

        var now = clock.GetUtcNow();
        var artifact = new Artifact(
            artifactId,
            workspaceId,
            userId,
            normalizedFileName,
            normalizedContentType,
            staged.SizeBytes,
            staged.Sha256,
            staged.StorageKey,
            now,
            workflowRunId,
            stepRunId,
            staged.StagingKey,
            normalizedIdempotency);

        ArtifactAddOutcome addOutcome;
        try
        {
            addOutcome = await artifacts.TryAddWithinQuotaAsync(
                artifact,
                policy.MaxArtifactsPerWorkspace,
                policy.MaxWorkspaceBytes,
                cancellationToken);
        }
        catch
        {
            await SafeDeleteStagingAsync(
                staged.StagingKey,
                cancellationToken);
            throw;
        }

        if (addOutcome != ArtifactAddOutcome.Added)
        {
            await SafeDeleteStagingAsync(
                staged.StagingKey,
                cancellationToken);

            if (addOutcome == ArtifactAddOutcome.WorkspaceMissing)
                return new(null, "workspace_not_found");

            if (addOutcome == ArtifactAddOutcome.QuotaExceeded)
                return new(null, "artifact_quota_exceeded");

            var existing = normalizedIdempotency is null
                ? null
                : await artifacts.FindByIdempotencyKeyAsync(
                    workspaceId,
                    normalizedIdempotency,
                    cancellationToken);

            if (existing is null)
                return new(null, "idempotency_conflict");

            return Matches(existing, artifact) &&
                   existing.Status is ArtifactStatus.Pending or ArtifactStatus.Ready
                ? new(Map(existing), null, IsReplay: true)
                : new(null, "idempotency_conflict");
        }

        try
        {
            await store.CommitAsync(
                artifact.StagingKey!,
                artifact.StorageKey,
                artifact.SizeBytes,
                artifact.Sha256,
                cancellationToken);
        }
        catch (ArtifactStoreIntegrityException)
        {
            await MarkFailedAndCleanupAsync(
                artifact,
                staged.StagingKey,
                cancellationToken);
            return new(null, "artifact_integrity_failed");
        }
        catch (FileNotFoundException)
        {
            await MarkFailedAndCleanupAsync(
                artifact,
                staged.StagingKey,
                cancellationToken);
            return new(null, "artifact_storage_failed");
        }
        catch (IOException)
        {
            return new(Map(artifact), "artifact_storage_unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            return new(Map(artifact), "artifact_storage_unavailable");
        }

        artifact.MarkReady();

        await audit.AddAsync(
            new WorkflowAuditEvent(
                Guid.NewGuid(),
                workspaceId,
                WorkflowAuditEventType.ArtifactCreated,
                clock.GetUtcNow(),
                workflowRunId: workflowRunId,
                workflowStepRunId: stepRunId,
                actorUserId: userId,
                detailJson: JsonSerializer.Serialize(new
                {
                    artifactId = artifact.Id,
                    sizeBytes = artifact.SizeBytes
                })),
            cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new(Map(artifact), null);
    }
    public async Task<ArtifactResult<ArtifactContentHandle>> OpenContentAsync(
        Guid userId,
        Guid workspaceId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(
                userId,
                workspaceId,
                cancellationToken) is null)
            return new(null, "workspace_not_found");

        var artifact = await artifacts.FindAsync(
            workspaceId,
            artifactId,
            cancellationToken);

        if (artifact is null || artifact.Status == ArtifactStatus.Deleted)
            return new(null, "artifact_not_found");

        if (artifact.Status != ArtifactStatus.Ready)
            return new(null, "artifact_not_ready");

        try
        {
            var info = await store.GetInfoAsync(
                artifact.StorageKey,
                cancellationToken);

            if (info is null ||
                info.SizeBytes != artifact.SizeBytes ||
                !string.Equals(
                    info.Sha256,
                    artifact.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                return new(null, "artifact_integrity_failed");

            var stream = await store.OpenReadAsync(
                artifact.StorageKey,
                cancellationToken);

            if (stream is null)
                return new(null, "artifact_integrity_failed");

            return new(
                new ArtifactContentHandle(
                    stream,
                    artifact.FileName,
                    artifact.ContentType,
                    artifact.SizeBytes),
                null);
        }
        catch (ArtifactStoreIntegrityException)
        {
            return new(null, "artifact_integrity_failed");
        }
        catch (IOException)
        {
            return new(null, "artifact_storage_unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            return new(null, "artifact_storage_unavailable");
        }
    }

    public async Task<ArtifactResult<ArtifactView>> DeleteAsync(
        Guid userId,
        Guid workspaceId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        var membership = await workspaces.FindMembershipAsync(
            userId,
            workspaceId,
            cancellationToken);

        if (membership is null)
            return new(null, "workspace_not_found");

        var artifact = await artifacts.FindAsync(
            workspaceId,
            artifactId,
            cancellationToken);

        if (artifact is null)
            return new(null, "artifact_not_found");

        if (artifact.CreatedByUserId != userId &&
            membership.Role < WorkspaceRole.Admin)
            return new(null, "forbidden");

        if (artifact.Status != ArtifactStatus.Deleted)
        {
            artifact.MarkDeleted(clock.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        await TryDeletePhysicalAsync(
            artifact,
            cancellationToken);

        return new(Map(artifact), null);
    }

    private async Task TryDeletePhysicalAsync(
        Artifact artifact,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.DeleteAsync(
                artifact.StorageKey,
                cancellationToken);

            if (artifact.StagingKey is not null)
            {
                await store.DeleteStagingAsync(
                    artifact.StagingKey,
                    cancellationToken);
            }

            var remaining = await store.GetInfoAsync(
                artifact.StorageKey,
                cancellationToken);

            if (remaining is null &&
                artifact.StorageDeletedAtUtc is null)
            {
                artifact.MarkStorageDeleted(clock.GetUtcNow());
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (ArtifactStoreIntegrityException)
        {
        }
    }

    private async Task MarkFailedAndCleanupAsync(
        Artifact artifact,
        string stagingKey,
        CancellationToken cancellationToken)
    {
        artifact.MarkFailed(clock.GetUtcNow());

        try
        {
            await store.DeleteStagingAsync(
                stagingKey,
                cancellationToken);
            artifact.MarkStagingCleaned();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (ArtifactStoreIntegrityException)
        {
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task SafeDeleteStagingAsync(
        string stagingKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.DeleteStagingAsync(
                stagingKey,
                cancellationToken);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (ArtifactStoreIntegrityException)
        {
        }
    }
    private static string? NormalizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        if (separator >= 0)
            normalized = normalized[(separator + 1)..];

        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized is "." or ".." ||
            normalized.Any(char.IsControl))
            return null;

        return normalized;
    }

    private static string? NormalizeContentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "application/octet-stream";

        var normalized = value.Trim();
        if (normalized.Length > MaxContentTypeLength ||
            normalized.Any(char.IsControl) ||
            !MediaTypeHeaderValue.TryParse(
                normalized,
                out var parsed) ||
            string.IsNullOrWhiteSpace(parsed.MediaType))
            return null;

        var canonical = parsed.ToString();
        return canonical.Length <= MaxContentTypeLength
            ? canonical
            : null;
    }

    private static string? NormalizeIdempotencyKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        if (normalized.Length is < 1 or > MaxIdempotencyKeyLength)
            return null;

        foreach (var character in normalized)
        {
            if (!(char.IsAsciiLetterOrDigit(character) ||
                  character is '.' or '_' or ':' or '-'))
                return null;
        }

        return normalized;
    }

    private static bool Matches(
        Artifact existing,
        Artifact proposed) =>
        string.Equals(
            existing.FileName,
            proposed.FileName,
            StringComparison.Ordinal) &&
        string.Equals(
            existing.ContentType,
            proposed.ContentType,
            StringComparison.OrdinalIgnoreCase) &&
        existing.SizeBytes == proposed.SizeBytes &&
        string.Equals(
            existing.Sha256,
            proposed.Sha256,
            StringComparison.OrdinalIgnoreCase) &&
        existing.WorkflowRunId == proposed.WorkflowRunId &&
        existing.StepRunId == proposed.StepRunId;

    public static ArtifactView Map(Artifact artifact) =>
        new(
            artifact.Id,
            artifact.FileName,
            artifact.ContentType,
            artifact.SizeBytes,
            artifact.Sha256,
            artifact.Status,
            artifact.CreatedByUserId,
            artifact.WorkflowRunId,
            artifact.StepRunId,
            artifact.CreatedAtUtc,
            artifact.FailedAtUtc,
            artifact.DeletedAtUtc);
}
