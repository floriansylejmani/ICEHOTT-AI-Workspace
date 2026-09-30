using System.Text;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Tests;

// Provider-unavailable and cancellation behaviour of the artifact services when the
// object store misbehaves. Uses a real PostgreSQL database and a faulting store
// decorator; skipped locally when ICEHOTT_POSTGRES_TEST_CONNECTION is unset.
public sealed partial class ArtifactServicePostgresTests
{
    private static ArtifactStoreUnavailableException Unavailable() =>
        new("provider down");

    [Fact]
    public async Task Stage_Provider_Unavailable_Returns_Storage_Unavailable_And_Persists_Nothing()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();
        using var root = new TempArtifactRoot();

        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var world = await SeedWorldAsync(isolatedConnection, clock.GetUtcNow());
            var store = new FaultyStore(CreateStore(root.Path)) { StageFault = Unavailable() };

            await using (var db = CreateContext(isolatedConnection))
            {
                var bytes = Encoding.UTF8.GetBytes("stage down");
                await using var source = new MemoryStream(bytes);
                var result = await CreateService(db, store, DefaultPolicy(), clock).UploadAsync(
                    world.OwnerA, world.WorkspaceA, "a.txt", "text/plain", bytes.Length, source, "k1");

                Assert.False(result.Succeeded);
                Assert.Equal("artifact_storage_unavailable", result.ErrorCode);
            }

            await using var verify = CreateContext(isolatedConnection);
            Assert.Equal(0, await verify.Artifacts.CountAsync());
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Commit_Provider_Unavailable_Leaves_Pending_And_Maintenance_Defers_Then_Recovers()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();
        using var root = new TempArtifactRoot();

        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var world = await SeedWorldAsync(isolatedConnection, clock.GetUtcNow());
            var store = new FaultyStore(CreateStore(root.Path)) { CommitFault = Unavailable() };
            var policy = DefaultPolicy();
            Guid artifactId;

            await using (var db = CreateContext(isolatedConnection))
            {
                var bytes = Encoding.UTF8.GetBytes("commit down");
                await using var source = new MemoryStream(bytes);
                var result = await CreateService(db, store, policy, clock).UploadAsync(
                    world.OwnerA, world.WorkspaceA, "b.txt", "text/plain", bytes.Length, source, "k2");

                Assert.Equal("artifact_storage_unavailable", result.ErrorCode);
                Assert.NotNull(result.Value);
                Assert.Equal(ArtifactStatus.Pending, result.Value!.Status);
                artifactId = result.Value.Id;
            }

            clock.Advance(TimeSpan.FromSeconds(31));

            // Provider still down: recovery is deferred, never failed.
            store.CleanupFault = Unavailable();
            await using (var db = CreateContext(isolatedConnection))
            {
                var deferred = await CreateMaintenance(db, store, policy, clock).RunOnceAsync();
                Assert.Equal(0, deferred.RecoveredReady);
                Assert.Equal(0, deferred.MarkedFailed);
                Assert.Equal(0, deferred.OrphanStagingDeleted);
            }

            await using (var verify = CreateContext(isolatedConnection))
                Assert.Equal(
                    ArtifactStatus.Pending,
                    (await verify.Artifacts.SingleAsync(x => x.Id == artifactId)).Status);

            // Provider back: the same Pending artifact is recovered from staging.
            store.CommitFault = null;
            store.CleanupFault = null;
            await using (var db = CreateContext(isolatedConnection))
            {
                var recovered = await CreateMaintenance(db, store, policy, clock).RunOnceAsync();
                Assert.Equal(1, recovered.RecoveredReady);
            }

            await using var final = CreateContext(isolatedConnection);
            Assert.Equal(
                ArtifactStatus.Ready,
                (await final.Artifacts.SingleAsync(x => x.Id == artifactId)).Status);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Read_Provider_Unavailable_Returns_Storage_Unavailable()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();
        using var root = new TempArtifactRoot();

        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var world = await SeedWorldAsync(isolatedConnection, clock.GetUtcNow());
            var store = new FaultyStore(CreateStore(root.Path));
            Guid artifactId;

            await using (var db = CreateContext(isolatedConnection))
            {
                var bytes = Encoding.UTF8.GetBytes("read down");
                await using var source = new MemoryStream(bytes);
                var upload = await CreateService(db, store, DefaultPolicy(), clock).UploadAsync(
                    world.OwnerA, world.WorkspaceA, "c.txt", "text/plain", bytes.Length, source, "k3");
                Assert.True(upload.Succeeded);
                artifactId = upload.Value!.Id;
            }

            store.ReadFault = Unavailable();
            await using (var db = CreateContext(isolatedConnection))
            {
                var read = await CreateService(db, store, DefaultPolicy(), clock).OpenContentAsync(
                    world.OwnerA, world.WorkspaceA, artifactId);
                Assert.False(read.Succeeded);
                Assert.Equal("artifact_storage_unavailable", read.ErrorCode);
            }
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Physical_Delete_Unavailable_Stays_Durably_Pending_For_Maintenance()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();
        using var root = new TempArtifactRoot();

        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var world = await SeedWorldAsync(isolatedConnection, clock.GetUtcNow());
            var inner = CreateStore(root.Path);
            var store = new FaultyStore(inner);
            var policy = DefaultPolicy();
            Guid artifactId;
            string storageKey;

            await using (var db = CreateContext(isolatedConnection))
            {
                var bytes = Encoding.UTF8.GetBytes("delete down");
                await using var source = new MemoryStream(bytes);
                var upload = await CreateService(db, store, policy, clock).UploadAsync(
                    world.OwnerA, world.WorkspaceA, "d.txt", "text/plain", bytes.Length, source, "k4");
                Assert.True(upload.Succeeded);
                artifactId = upload.Value!.Id;
                storageKey = (await db.Artifacts.SingleAsync(x => x.Id == artifactId)).StorageKey;
            }

            store.DeleteFault = Unavailable();
            await using (var db = CreateContext(isolatedConnection))
            {
                var deleted = await CreateService(db, store, policy, clock).DeleteAsync(
                    world.OwnerA, world.WorkspaceA, artifactId);
                Assert.True(deleted.Succeeded);
            }

            await using (var verify = CreateContext(isolatedConnection))
            {
                var row = await verify.Artifacts.SingleAsync(x => x.Id == artifactId);
                Assert.Equal(ArtifactStatus.Deleted, row.Status);
                Assert.Null(row.StorageDeletedAtUtc);
            }

            Assert.NotNull(await inner.GetInfoAsync(storageKey));

            await using (var db = CreateContext(isolatedConnection))
            {
                var deferred = await CreateMaintenance(db, store, policy, clock).RunOnceAsync();
                Assert.Equal(0, deferred.PhysicalObjectsDeleted);
            }

            store.DeleteFault = null;
            await using (var db = CreateContext(isolatedConnection))
            {
                var done = await CreateMaintenance(db, store, policy, clock).RunOnceAsync();
                Assert.Equal(1, done.PhysicalObjectsDeleted);
            }

            Assert.Null(await inner.GetInfoAsync(storageKey));
            await using var final = CreateContext(isolatedConnection);
            Assert.NotNull(
                (await final.Artifacts.SingleAsync(x => x.Id == artifactId)).StorageDeletedAtUtc);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Cancellation_Propagates_Through_Service_And_Maintenance()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();
        using var root = new TempArtifactRoot();

        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var world = await SeedWorldAsync(isolatedConnection, clock.GetUtcNow());
            var store = new FaultyStore(CreateStore(root.Path));
            var policy = DefaultPolicy();
            var bytes = Encoding.UTF8.GetBytes("cancel me");

            store.StageFault = new OperationCanceledException();
            await using (var db = CreateContext(isolatedConnection))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    await using var source = new MemoryStream(bytes);
                    await CreateService(db, store, policy, clock).UploadAsync(
                        world.OwnerA, world.WorkspaceA, "e.txt", "text/plain", bytes.Length, source, "k5");
                });
            }

            store.StageFault = null;
            store.CommitFault = new OperationCanceledException();
            await using (var db = CreateContext(isolatedConnection))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    await using var source = new MemoryStream(bytes);
                    await CreateService(db, store, policy, clock).UploadAsync(
                        world.OwnerA, world.WorkspaceA, "f.txt", "text/plain", bytes.Length, source, "k6");
                });
            }

            // The interrupted upload is left Pending (recoverable), not failed.
            await using (var verify = CreateContext(isolatedConnection))
                Assert.Equal(
                    ArtifactStatus.Pending,
                    (await verify.Artifacts.SingleAsync()).Status);

            clock.Advance(TimeSpan.FromSeconds(31));
            await using (var db = CreateContext(isolatedConnection))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => CreateMaintenance(db, store, policy, clock).RunOnceAsync());
            }

            store.CommitFault = null;
            store.CleanupFault = new OperationCanceledException();
            await using (var db = CreateContext(isolatedConnection))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => CreateMaintenance(db, store, policy, clock).RunOnceAsync());
            }
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    private sealed class FaultyStore(IArtifactStore inner) : IArtifactStore
    {
        public Exception? StageFault { get; set; }
        public Exception? CommitFault { get; set; }
        public Exception? ReadFault { get; set; }
        public Exception? DeleteFault { get; set; }
        public Exception? CleanupFault { get; set; }

        public Task<ArtifactStageResult> StageAsync(
            Guid workspaceId,
            Guid artifactId,
            Stream source,
            long maxBytes,
            CancellationToken cancellationToken = default)
        {
            Raise(StageFault);
            return inner.StageAsync(workspaceId, artifactId, source, maxBytes, cancellationToken);
        }

        public Task CommitAsync(
            string stagingKey,
            string storageKey,
            long expectedSizeBytes,
            string expectedSha256,
            CancellationToken cancellationToken = default)
        {
            Raise(CommitFault);
            return inner.CommitAsync(
                stagingKey, storageKey, expectedSizeBytes, expectedSha256, cancellationToken);
        }

        public Task<ArtifactStoredObjectInfo?> GetInfoAsync(
            string storageKey,
            CancellationToken cancellationToken = default)
        {
            Raise(ReadFault);
            return inner.GetInfoAsync(storageKey, cancellationToken);
        }

        public Task<Stream?> OpenReadAsync(
            string storageKey,
            CancellationToken cancellationToken = default)
        {
            Raise(ReadFault);
            return inner.OpenReadAsync(storageKey, cancellationToken);
        }

        public Task DeleteAsync(
            string storageKey,
            CancellationToken cancellationToken = default)
        {
            Raise(DeleteFault);
            return inner.DeleteAsync(storageKey, cancellationToken);
        }

        public Task DeleteStagingAsync(
            string stagingKey,
            CancellationToken cancellationToken = default) =>
            inner.DeleteStagingAsync(stagingKey, cancellationToken);

        public Task<int> CleanupStagingAsync(
            DateTimeOffset olderThanUtc,
            int maxItems,
            CancellationToken cancellationToken = default)
        {
            Raise(CleanupFault);
            return inner.CleanupStagingAsync(olderThanUtc, maxItems, cancellationToken);
        }

        private static void Raise(Exception? fault)
        {
            if (fault is not null)
                throw fault;
        }
    }
}
