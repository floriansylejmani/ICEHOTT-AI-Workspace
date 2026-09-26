using System.Security.Cryptography;
using System.Text;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Artifacts;
using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Infrastructure.Artifacts;
using ICEHOTT.Persistence;
using ICEHOTT.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ICEHOTT.Tests;

public sealed class ArtifactServicePostgresTests
{
    private const string Phase5CMigration =
        "20260926154306_Phase5CDurableRunner";

    private static string? Connection =>
        Environment.GetEnvironmentVariable("ICEHOTT_POSTGRES_TEST_CONNECTION");

    [Fact]
    public async Task Upload_Download_RoundTrip_Is_Workspace_Scoped()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();
        using var root = new TempArtifactRoot();

        try
        {
            var clock = new MutableTimeProvider(
                new DateTimeOffset(2026, 9, 26, 18, 0, 0, TimeSpan.Zero));
            var world = await SeedWorldAsync(isolatedConnection, clock.GetUtcNow());
            var store = CreateStore(root.Path);
            var policy = DefaultPolicy();

            await using var db = CreateContext(isolatedConnection);
            var service = CreateService(db, store, policy, clock);
            var bytes = Encoding.UTF8.GetBytes("artifact round trip");

            await using var source = new MemoryStream(bytes, writable: false);
            var upload = await service.UploadAsync(
                world.OwnerA,
                world.WorkspaceA,
                "..\\..\\report.txt",
                "text/plain",
                bytes.Length,
                source,
                "roundtrip-1");

            Assert.True(upload.Succeeded);
            Assert.False(upload.IsReplay);
            Assert.Equal(ArtifactStatus.Ready, upload.Value!.Status);
            Assert.Equal("report.txt", upload.Value.FileName);
            Assert.Equal(Sha(bytes), upload.Value.Sha256);

            var content = await service.OpenContentAsync(
                world.OwnerA,
                world.WorkspaceA,
                upload.Value.Id);

            Assert.True(content.Succeeded);
            await using var stream = content.Value!.Content;
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());

            var crossTenant = await service.OpenContentAsync(
                world.OwnerB,
                world.WorkspaceA,
                upload.Value.Id);

            Assert.False(crossTenant.Succeeded);
            Assert.Equal("workspace_not_found", crossTenant.ErrorCode);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Same_Idempotency_Key_Replays_Without_Duplicate()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();
        using var root = new TempArtifactRoot();

        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var world = await SeedWorldAsync(isolatedConnection, clock.GetUtcNow());
            var store = CreateStore(root.Path);
            var bytes = Encoding.UTF8.GetBytes("same payload");

            Guid firstId;
            await using (var db = CreateContext(isolatedConnection))
            {
                var service = CreateService(db, store, DefaultPolicy(), clock);
                await using var source = new MemoryStream(bytes, writable: false);
                var first = await service.UploadAsync(
                    world.OwnerA,
                    world.WorkspaceA,
                    "same.txt",
                    "text/plain",
                    bytes.Length,
                    source,
                    "stable-key");

                Assert.True(first.Succeeded);
                firstId = first.Value!.Id;
            }

            await using (var db = CreateContext(isolatedConnection))
            {
                var service = CreateService(db, store, DefaultPolicy(), clock);
                await using var source = new MemoryStream(bytes, writable: false);
                var replay = await service.UploadAsync(
                    world.OwnerA,
                    world.WorkspaceA,
                    "same.txt",
                    "text/plain",
                    bytes.Length,
                    source,
                    "stable-key");

                Assert.True(replay.Succeeded);
                Assert.True(replay.IsReplay);
                Assert.Equal(firstId, replay.Value!.Id);
            }

            await using var verify = CreateContext(isolatedConnection);
            Assert.Equal(
                1,
                await verify.Artifacts.CountAsync(
                    x => x.WorkspaceId == world.WorkspaceA));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Same_Idempotency_Key_With_Different_Content_Conflicts()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();
        using var root = new TempArtifactRoot();

        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var world = await SeedWorldAsync(isolatedConnection, clock.GetUtcNow());
            var store = CreateStore(root.Path);

            await using (var db = CreateContext(isolatedConnection))
            {
                var service = CreateService(db, store, DefaultPolicy(), clock);
                var bytes = Encoding.UTF8.GetBytes("first");
                await using var source = new MemoryStream(bytes, writable: false);

                var result = await service.UploadAsync(
                    world.OwnerA,
                    world.WorkspaceA,
                    "same.txt",
                    "text/plain",
                    bytes.Length,
                    source,
                    "same-key");

                Assert.True(result.Succeeded);
            }

            await using (var db = CreateContext(isolatedConnection))
            {
                var service = CreateService(db, store, DefaultPolicy(), clock);
                var bytes = Encoding.UTF8.GetBytes("different");
                await using var source = new MemoryStream(bytes, writable: false);

                var result = await service.UploadAsync(
                    world.OwnerA,
                    world.WorkspaceA,
                    "same.txt",
                    "text/plain",
                    bytes.Length,
                    source,
                    "same-key");

                Assert.False(result.Succeeded);
                Assert.Equal("idempotency_conflict", result.ErrorCode);
            }
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task Concurrent_Uploads_Respect_Count_Quota()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();
        using var root = new TempArtifactRoot();

        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var world = await SeedWorldAsync(isolatedConnection, clock.GetUtcNow());
            var store = CreateStore(root.Path);
            var policy = DefaultPolicy() with
            {
                MaxArtifactsPerWorkspace = 1
            };

            async Task<ArtifactResult<ArtifactView>> UploadAsync(
                string key,
                string content)
            {
                await using var db = CreateContext(isolatedConnection);
                var service = CreateService(db, store, policy, clock);
                var bytes = Encoding.UTF8.GetBytes(content);
                await using var source = new MemoryStream(bytes, writable: false);

                return await service.UploadAsync(
                    world.OwnerA,
                    world.WorkspaceA,
                    $"{key}.txt",
                    "text/plain",
                    bytes.Length,
                    source,
                    key);
            }

            var results = await Task.WhenAll(
                UploadAsync("quota-a", "A"),
                UploadAsync("quota-b", "B"));

            Assert.Single(results, x => x.Succeeded);
            Assert.Single(
                results,
                x => x.ErrorCode == "artifact_quota_exceeded");

            await using var verify = CreateContext(isolatedConnection);
            Assert.Equal(
                1,
                await verify.Artifacts.CountAsync(
                    x =>
                        x.WorkspaceId == world.WorkspaceA &&
                        x.Status == ArtifactStatus.Ready));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Delete_Requires_Creator_Or_Admin_And_Removes_Bytes()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();
        using var root = new TempArtifactRoot();

        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var world = await SeedWorldAsync(isolatedConnection, clock.GetUtcNow());
            var store = CreateStore(root.Path);
            var policy = DefaultPolicy();

            Guid artifactId;
            string storageKey;

            await using (var db = CreateContext(isolatedConnection))
            {
                var service = CreateService(db, store, policy, clock);
                var bytes = Encoding.UTF8.GetBytes("delete me");
                await using var source = new MemoryStream(bytes, writable: false);

                var upload = await service.UploadAsync(
                    world.MemberA,
                    world.WorkspaceA,
                    "delete.txt",
                    "text/plain",
                    bytes.Length,
                    source,
                    "delete-key");

                Assert.True(upload.Succeeded);
                artifactId = upload.Value!.Id;
                storageKey = (await db.Artifacts.SingleAsync(
                    x => x.Id == artifactId)).StorageKey;
            }

            await using (var db = CreateContext(isolatedConnection))
            {
                var service = CreateService(db, store, policy, clock);
                var forbidden = await service.DeleteAsync(
                    world.MemberB,
                    world.WorkspaceA,
                    artifactId);

                Assert.False(forbidden.Succeeded);
                Assert.Equal("forbidden", forbidden.ErrorCode);
            }

            await using (var db = CreateContext(isolatedConnection))
            {
                var service = CreateService(db, store, policy, clock);
                var deleted = await service.DeleteAsync(
                    world.OwnerA,
                    world.WorkspaceA,
                    artifactId);

                Assert.True(deleted.Succeeded);
            }

            Assert.Null(await store.GetInfoAsync(storageKey));

            await using var verify = CreateContext(isolatedConnection);
            var artifact = await verify.Artifacts.SingleAsync(
                x => x.Id == artifactId);

            Assert.Equal(ArtifactStatus.Deleted, artifact.Status);
            Assert.NotNull(artifact.StorageDeletedAtUtc);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task Maintenance_Recovers_Pending_From_Staging_After_Crash()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();
        using var root = new TempArtifactRoot();

        try
        {
            var clock = new MutableTimeProvider(
                new DateTimeOffset(2026, 9, 26, 20, 0, 0, TimeSpan.Zero));
            var world = await SeedWorldAsync(isolatedConnection, clock.GetUtcNow());
            var store = CreateStore(root.Path);
            var policy = DefaultPolicy() with
            {
                PendingRecoverySeconds = 30
            };

            var bytes = Encoding.UTF8.GetBytes("recover staged");
            var artifactId = Guid.NewGuid();
            ArtifactStageResult staged;

            await using (var source = new MemoryStream(bytes, writable: false))
            {
                staged = await store.StageAsync(
                    world.WorkspaceA,
                    artifactId,
                    source,
                    policy.MaxArtifactBytes);
            }

            await using (var db = CreateContext(isolatedConnection))
            {
                var artifact = new Artifact(
                    artifactId,
                    world.WorkspaceA,
                    world.OwnerA,
                    "recover.txt",
                    "text/plain",
                    staged.SizeBytes,
                    staged.Sha256,
                    staged.StorageKey,
                    clock.GetUtcNow(),
                    stagingKey: staged.StagingKey,
                    idempotencyKey: "recover-key");

                var repo = new ArtifactRepository(db);
                Assert.Equal(
                    ArtifactAddOutcome.Added,
                    await repo.TryAddWithinQuotaAsync(
                        artifact,
                        policy.MaxArtifactsPerWorkspace,
                        policy.MaxWorkspaceBytes));
            }

            clock.Advance(TimeSpan.FromSeconds(31));

            await using (var db = CreateContext(isolatedConnection))
            {
                var maintenance = CreateMaintenance(
                    db,
                    store,
                    policy,
                    clock);

                var result = await maintenance.RunOnceAsync();
                Assert.Equal(1, result.RecoveredReady);
            }

            await using var verify = CreateContext(isolatedConnection);
            var recovered = await verify.Artifacts.SingleAsync(
                x => x.Id == artifactId);

            Assert.Equal(ArtifactStatus.Ready, recovered.Status);
            Assert.Null(recovered.StagingKey);
            Assert.NotNull(await store.GetInfoAsync(recovered.StorageKey));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Maintenance_Finalizes_Object_Moved_Before_Db_Ready_Save()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();
        using var root = new TempArtifactRoot();

        try
        {
            var clock = new MutableTimeProvider(
                new DateTimeOffset(2026, 9, 26, 21, 0, 0, TimeSpan.Zero));
            var world = await SeedWorldAsync(isolatedConnection, clock.GetUtcNow());
            var store = CreateStore(root.Path);
            var policy = DefaultPolicy() with
            {
                PendingRecoverySeconds = 30
            };

            var bytes = Encoding.UTF8.GetBytes("already moved");
            var artifactId = Guid.NewGuid();
            ArtifactStageResult staged;

            await using (var source = new MemoryStream(bytes, writable: false))
            {
                staged = await store.StageAsync(
                    world.WorkspaceA,
                    artifactId,
                    source,
                    policy.MaxArtifactBytes);
            }

            await using (var db = CreateContext(isolatedConnection))
            {
                var artifact = new Artifact(
                    artifactId,
                    world.WorkspaceA,
                    world.OwnerA,
                    "moved.txt",
                    "text/plain",
                    staged.SizeBytes,
                    staged.Sha256,
                    staged.StorageKey,
                    clock.GetUtcNow(),
                    stagingKey: staged.StagingKey,
                    idempotencyKey: "moved-key");

                var repo = new ArtifactRepository(db);
                Assert.Equal(
                    ArtifactAddOutcome.Added,
                    await repo.TryAddWithinQuotaAsync(
                        artifact,
                        policy.MaxArtifactsPerWorkspace,
                        policy.MaxWorkspaceBytes));
            }

            await store.CommitAsync(
                staged.StagingKey,
                staged.StorageKey,
                staged.SizeBytes,
                staged.Sha256);

            clock.Advance(TimeSpan.FromSeconds(31));

            await using (var db = CreateContext(isolatedConnection))
            {
                var maintenance = CreateMaintenance(
                    db,
                    store,
                    policy,
                    clock);
                var result = await maintenance.RunOnceAsync();

                Assert.Equal(1, result.RecoveredReady);
            }

            await using var verify = CreateContext(isolatedConnection);
            Assert.Equal(
                ArtifactStatus.Ready,
                (await verify.Artifacts.SingleAsync(
                    x => x.Id == artifactId)).Status);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task Phase5D_Migration_Rolls_Back_To_5C_And_Reapplies()
    {
        if (string.IsNullOrWhiteSpace(Connection)) return;
        var (databaseName, isolatedConnection) = await CreateIsolatedDatabaseAsync();

        try
        {
            await using var db = CreateContext(isolatedConnection);
            await db.Database.MigrateAsync();

            Assert.True(await ColumnExistsAsync(
                db,
                "artifacts",
                "StagingKey"));
            Assert.True(await ColumnExistsAsync(
                db,
                "artifacts",
                "StorageDeletedAtUtc"));

            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(Phase5CMigration);

            Assert.False(await ColumnExistsAsync(
                db,
                "artifacts",
                "StagingKey"));
            Assert.True(await TableExistsAsync(
                db,
                "artifacts"));

            await migrator.MigrateAsync();

            Assert.True(await ColumnExistsAsync(
                db,
                "artifacts",
                "StagingKey"));
            Assert.True(await ColumnExistsAsync(
                db,
                "artifacts",
                "IdempotencyKey"));
        }
        finally
        {
            await DropIsolatedDatabaseAsync(databaseName);
        }
    }

    private sealed record World(
        Guid OwnerA,
        Guid OwnerB,
        Guid MemberA,
        Guid MemberB,
        Guid WorkspaceA,
        Guid WorkspaceB);

    private static async Task<World> SeedWorldAsync(
        string connectionString,
        DateTimeOffset now)
    {
        await using var db = CreateContext(connectionString);
        await db.Database.MigrateAsync();

        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var memberA = Guid.NewGuid();
        var memberB = Guid.NewGuid();
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();

        db.Users.AddRange(
            NewUser(ownerA, "Owner A", now),
            NewUser(ownerB, "Owner B", now),
            NewUser(memberA, "Member A", now),
            NewUser(memberB, "Member B", now));

        db.Workspaces.AddRange(
            new Workspace(
                workspaceA,
                "Artifacts A",
                $"artifacts-a-{workspaceA:N}",
                ownerA,
                now),
            new Workspace(
                workspaceB,
                "Artifacts B",
                $"artifacts-b-{workspaceB:N}",
                ownerB,
                now));

        db.WorkspaceMemberships.AddRange(
            new WorkspaceMembership(
                workspaceA,
                ownerA,
                WorkspaceRole.Owner,
                now),
            new WorkspaceMembership(
                workspaceA,
                memberA,
                WorkspaceRole.Member,
                now),
            new WorkspaceMembership(
                workspaceA,
                memberB,
                WorkspaceRole.Member,
                now),
            new WorkspaceMembership(
                workspaceB,
                ownerB,
                WorkspaceRole.Owner,
                now));

        await db.SaveChangesAsync();

        return new(
            ownerA,
            ownerB,
            memberA,
            memberB,
            workspaceA,
            workspaceB);
    }
    private static User NewUser(
        Guid id,
        string displayName,
        DateTimeOffset now) =>
        new(
            id,
            $"{id:N}@artifact.test",
            displayName,
            "not-a-real-hash",
            now);

    private static ArtifactService CreateService(
        ICEHOTTDbContext db,
        LocalArtifactStore store,
        ArtifactPolicy policy,
        TimeProvider clock) =>
        new(
            new WorkspaceRepository(db),
            new ArtifactRepository(db),
            store,
            new WorkflowAuditRepository(db),
            db,
            policy,
            clock);

    private static ArtifactMaintenanceService CreateMaintenance(
        ICEHOTTDbContext db,
        LocalArtifactStore store,
        ArtifactPolicy policy,
        TimeProvider clock) =>
        new(
            new ArtifactRepository(db),
            store,
            new WorkflowAuditRepository(db),
            db,
            policy,
            clock);

    private static LocalArtifactStore CreateStore(string rootPath) =>
        new(
            Options.Create(
                new ArtifactStorageOptions
                {
                    RootPath = rootPath
                }));

    private static ArtifactPolicy DefaultPolicy() =>
        new(
            MaxArtifactBytes: 1024 * 1024,
            MaxWorkspaceBytes: 4 * 1024 * 1024,
            MaxArtifactsPerWorkspace: 100,
            MaintenanceIntervalSeconds: 30,
            PendingRecoverySeconds: 30,
            StagingRetentionMinutes: 1);

    private static string Sha(byte[] bytes) =>
        Convert.ToHexString(
                SHA256.HashData(bytes))
            .ToLowerInvariant();

    private static Task<bool> TableExistsAsync(
        ICEHOTTDbContext db,
        string tableName) =>
        db.Database.SqlQueryRaw<bool>(
            """
            SELECT to_regclass({0}) IS NOT NULL AS "Value"
            """,
            $"public.{tableName}").SingleAsync();

    private static Task<bool> ColumnExistsAsync(
        ICEHOTTDbContext db,
        string tableName,
        string columnName) =>
        db.Database.SqlQueryRaw<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = {0}
                  AND column_name = {1}
            ) AS "Value"
            """,
            tableName,
            columnName).SingleAsync();

    private static async Task<(string DatabaseName, string ConnectionString)>
        CreateIsolatedDatabaseAsync()
    {
        var source = new NpgsqlConnectionStringBuilder(Connection!);
        var databaseName = $"icehott_artifact_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(Connection!)
        {
            Database = "postgres",
            Pooling = false
        };

        await using var connection = new NpgsqlConnection(
            admin.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            $"CREATE DATABASE \"{databaseName}\"",
            connection);
        await command.ExecuteNonQueryAsync();

        source.Database = databaseName;
        source.Pooling = false;

        return (databaseName, source.ConnectionString);
    }

    private static async Task DropIsolatedDatabaseAsync(
        string databaseName)
    {
        var admin = new NpgsqlConnectionStringBuilder(Connection!)
        {
            Database = "postgres",
            Pooling = false
        };

        await using var connection = new NpgsqlConnection(
            admin.ConnectionString);
        await connection.OpenAsync();

        await using (var terminate = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            "WHERE datname = @name AND pid <> pg_backend_pid()",
            connection))
        {
            terminate.Parameters.AddWithValue(
                "name",
                databaseName);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{databaseName}\"",
            connection);
        await drop.ExecuteNonQueryAsync();
    }

    private static ICEHOTTDbContext CreateContext(
        string connectionString) =>
        new(
            new DbContextOptionsBuilder<ICEHOTTDbContext>()
                .UseNpgsql(connectionString)
                .Options);

    private sealed class TempArtifactRoot : IDisposable
    {
        public TempArtifactRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "icehott-artifact-pg-tests",
                Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class MutableTimeProvider(
        DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration)
        {
            _now = _now.Add(duration);
        }
    }
}
