using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Knowledge;
using ICEHOTT.Domain.Knowledge;
using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Infrastructure.Ai;
using ICEHOTT.Persistence;
using ICEHOTT.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

const string datasetRelativePath = "evals/rag/v2/dataset.json";
const string hashRelativePath = "evals/rag/v2/dataset.sha256";
const string runnerVersion = "phase-3.6b-live-v1";

var mode = args.Length == 1 ? args[0].Trim().ToLowerInvariant() : "";
if (mode is not "--dry-run" and not "--live")
{
    PrintUsage();
    return 2;
}

var repoRoot = FindRepoRoot();
var datasetPath = Path.Combine(repoRoot, datasetRelativePath.Replace('/', Path.DirectorySeparatorChar));
var hashPath = Path.Combine(repoRoot, hashRelativePath.Replace('/', Path.DirectorySeparatorChar));
var dataset = await LoadAndVerifyDatasetAsync(datasetPath, hashPath);
var chunker = new StructureAwareKnowledgeChunker();
var corpusStats = ComputeCorpusStats(dataset, chunker);

if (mode == "--dry-run")
{
    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            mode = "dry-run",
            dataset = dataset.Version,
            datasetSha256 = dataset.Sha256,
            documents = dataset.Documents.Length,
            cases = dataset.Cases.Length,
            primaryChunks = corpusStats.PrimaryChunks,
            foreignChunks = corpusStats.ForeignChunks,
            expectedDocumentEmbeddingCalls = corpusStats.ExpectedDocumentEmbeddingCalls,
            expectedQueryEmbeddingCalls = dataset.Cases.Length,
            expectedTotalProviderCalls =
                corpusStats.ExpectedDocumentEmbeddingCalls + dataset.Cases.Length,
            liveRequirements = new[]
            {
                "ICEHOTT_BENCHMARK_CONNECTION",
                "ICEHOTT_LIVE_PROVIDER_BENCHMARK_OPT_IN",
                "OPENAI_API_KEY or ICEHOTT_OPENAI_API_KEY",
                "ICEHOTT_OPENAI_EMBEDDING_USD_PER_MILLION_INPUT_TOKENS",
                "ICEHOTT_LIVE_BENCHMARK_MAX_COST_USD"
            },
            activationPerformed = false
        },
        JsonOptions()));
    return 0;
}

var runId = Guid.NewGuid();
var artifactDirectory = Path.Combine(repoRoot, "artifacts", "benchmarks");
Directory.CreateDirectory(artifactDirectory);
var artifactPath = Path.Combine(
    artifactDirectory,
    $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{runId:N}.json");

try
{
    var connectionString = Environment.GetEnvironmentVariable(
        "ICEHOTT_BENCHMARK_CONNECTION");
    var optIn = Environment.GetEnvironmentVariable(
        "ICEHOTT_LIVE_PROVIDER_BENCHMARK_OPT_IN");
    var apiKey =
        Environment.GetEnvironmentVariable("ICEHOTT_OPENAI_API_KEY") ??
        Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    var baseUrl =
        Environment.GetEnvironmentVariable("ICEHOTT_OPENAI_BASE_URL") ??
        "https://api.openai.com/v1/";
    var timeoutSeconds = ParseOptionalInt(
        "ICEHOTT_OPENAI_TIMEOUT_SECONDS",
        30,
        min: 5,
        max: 120);
    var approvedMaxCostUsd = ParseRequiredDecimal(
        "ICEHOTT_LIVE_BENCHMARK_MAX_COST_USD");
    var usdPerMillionInputTokens = ParseRequiredDecimal(
        "ICEHOTT_OPENAI_EMBEDDING_USD_PER_MILLION_INPUT_TOKENS");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        await WriteFailureAsync(
            artifactPath,
            runId,
            dataset,
            "Configuration",
            "ICEHOTT_BENCHMARK_CONNECTION is required.");
        return 2;
    }

    var dbOptions = new DbContextOptionsBuilder<ICEHOTTDbContext>()
        .UseNpgsql(connectionString)
        .Options;

    await using var db = new ICEHOTTDbContext(dbOptions);
    var databaseName = db.Database.GetDbConnection().Database;

    LiveProviderBenchmarkGuard.ValidatePreflight(
            databaseName,
            optIn,
            apiKey,
            approvedMaxCostUsd,
            usdPerMillionInputTokens);

    await db.Database.MigrateAsync();

    LiveProviderBenchmarkGuard.ValidateFreshDatabase(
        await db.Users.LongCountAsync(),
        await db.Workspaces.LongCountAsync(),
        await db.KnowledgeDocuments.LongCountAsync());

    var seeded = await SeedDatasetAsync(
        db,
        dataset,
        chunker,
        runId);

    using var httpClient = new HttpClient
    {
        BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(timeoutSeconds)
    };

    var provider = new OpenAiEmbeddingProvider(
        httpClient,
        Options.Create(new OpenAiEmbeddingOptions
        {
            ApiKey = apiKey!,
            BaseUrl = baseUrl,
            TimeoutSeconds = timeoutSeconds
        }));

    IEmbeddingProviderRegistry providerRegistry =
        new EmbeddingProviderRegistry(
            new IEmbeddingProvider[] { provider });

    var profileRepository = new EmbeddingProfileRepository(db);
    var indexProvisioner = new PostgresVectorIndexProvisioner(db);
    var buildStore = new PostgresEmbeddingBuildStore(db);
    var vectorStore = new PostgresVectorStore(db);

    var profileManagement = new EmbeddingProfileManagementService(
        profileRepository,
        providerRegistry,
        indexProvisioner,
        db,
        TimeProvider.System);

    var profile = await profileManagement.CreateBuildingAsync(
        new CreateEmbeddingProfileCommand(
            Guid.NewGuid(),
            $"openai-small-rag-v2-{runId:N}"[..40],
            "openai",
            "text-embedding-3-small",
            1536,
            "1",
            2,
            "cosine",
            "unit"));

    var buildService = new EmbeddingProfileBuildService(
        new BuildEmbeddingProfileResolver(profileRepository),
        providerRegistry,
        indexProvisioner,
        buildStore,
        vectorStore);

    var build = await buildService.BuildUntilCompleteAsync(
        requestedBatchSize: 64,
        maxBatches: 100);

    if (!build.IsComplete ||
        build.MissingChunkCount != 0 ||
        build.EmbeddedChunkCount != build.ReadyChunkCount)
        throw new InvalidOperationException(
            "Candidate profile build did not reach complete coverage.");

    var retriever = new ProfileKnowledgeRetriever(
        providerRegistry,
        vectorStore,
        new HybridRagReranker(),
        new RetrievedContentPolicy());

    var benchmarkRunner = new RagBenchmarkRunner(
        retriever,
        new RagEvaluationEvidenceRepository(db),
        db,
        TimeProvider.System);

    var benchmarkDataset = new RagBenchmarkDataset(
        dataset.Version,
        dataset.Cases.Select(testCase =>
            new RagBenchmarkCase(
                testCase.Id,
                testCase.Query,
                testCase.ExpectedSources,
                testCase.ForbiddenSources,
                testCase.TopK,
                testCase.TenantForbiddenSources))
            .ToArray(),
        new RagBenchmarkThresholds(
            dataset.Thresholds.HitRateAtK,
            dataset.Thresholds.MeanRecallAtK,
            dataset.Thresholds.MeanPrecisionAtK,
            dataset.Thresholds.CitationCorrectness,
            dataset.Thresholds.TenantLeakageCount));

    var benchmark = await benchmarkRunner.RunAsync(
        seeded.PrimaryWorkspaceId,
        profile,
        benchmarkDataset,
        runnerVersion,
        new RagBenchmarkPricing(usdPerMillionInputTokens));

    var costWithinApprovedCap = true;
    string? costFailure = null;
    try
    {
        LiveProviderBenchmarkGuard.EnsureWithinApprovedCost(
            benchmark.EstimatedCostUsd,
            approvedMaxCostUsd);
    }
    catch (InvalidOperationException exception)
    {
        costWithinApprovedCap = false;
        costFailure = exception.Message;
    }

    var releaseGatePassed =
        benchmark.ThresholdsPassed &&
        benchmark.Evidence.TenantLeakageCount == 0 &&
        costWithinApprovedCap;

    await WriteJsonAsync(
        artifactPath,
        new
        {
            success = releaseGatePassed,
            runId,
            completedAtUtc = DateTimeOffset.UtcNow,
            dataset = new
            {
                version = dataset.Version,
                sha256 = dataset.Sha256,
                documents = dataset.Documents.Length,
                cases = dataset.Cases.Length
            },
            profile = new
            {
                profile.Id,
                profile.Key,
                profile.Provider,
                profile.Model,
                profile.Dimensions,
                profile.Version,
                profile.IndexVersion,
                profile.DistanceMetric,
                profile.Normalization,
                status = "Building"
            },
            corpus = new
            {
                seeded.PrimaryWorkspaceId,
                seeded.ForeignWorkspaceId,
                build.ReadyChunkCount,
                build.EmbeddedChunkCount,
                build.MissingChunkCount
            },
            benchmark = new
            {
                evidenceId = benchmark.Evidence.Id,
                benchmark.Evidence.HitRateAtK,
                benchmark.Evidence.MeanRecallAtK,
                benchmark.Evidence.MeanPrecisionAtK,
                benchmark.Evidence.CitationCorrectness,
                benchmark.Evidence.TenantLeakageCount,
                benchmark.InputTokens,
                benchmark.DurationMs,
                benchmark.EstimatedCostUsd,
                approvedMaxCostUsd,
                costWithinApprovedCap,
                costFailure,
                benchmark.ThresholdsPassed
            },
            promotion = new
            {
                offlineSemanticEvidenceRequired = true,
                activationPerformed = false,
                releaseGatePassed
            }
        });

    Console.WriteLine($"Benchmark artifact: {artifactPath}");
    Console.WriteLine(
        releaseGatePassed
            ? "LIVE_PROVIDER_BENCHMARK_PASS"
            : "LIVE_PROVIDER_BENCHMARK_FAIL");

    return releaseGatePassed ? 0 : 3;
}
catch (Exception exception)
{
    await WriteFailureAsync(
        artifactPath,
        runId,
        dataset,
        exception.GetType().Name,
        exception.Message);
    Console.Error.WriteLine(
        $"LIVE_PROVIDER_BENCHMARK_ERROR: {exception.GetType().Name}: {exception.Message}");
    Console.Error.WriteLine($"Benchmark artifact: {artifactPath}");
    return 4;
}

static async Task<SeedResult> SeedDatasetAsync(
    ICEHOTTDbContext db,
    LiveDataset dataset,
    IKnowledgeChunker chunker,
    Guid runId)
{
    var now = DateTimeOffset.UtcNow;
    var primaryUserId = Guid.NewGuid();
    var foreignUserId = Guid.NewGuid();
    var primaryWorkspaceId = Guid.NewGuid();
    var foreignWorkspaceId = Guid.NewGuid();

    db.Users.AddRange(
        new User(
            primaryUserId,
            $"benchmark-primary-{runId:N}@icehott.invalid",
            "Benchmark Primary",
            "benchmark-login-disabled",
            now),
        new User(
            foreignUserId,
            $"benchmark-foreign-{runId:N}@icehott.invalid",
            "Benchmark Foreign",
            "benchmark-login-disabled",
            now));

    db.Workspaces.AddRange(
        new Workspace(
            primaryWorkspaceId,
            "Benchmark Primary",
            $"benchmark-primary-{runId:N}"[..48],
            primaryUserId,
            now),
        new Workspace(
            foreignWorkspaceId,
            "Benchmark Foreign",
            $"benchmark-foreign-{runId:N}"[..48],
            foreignUserId,
            now));

    db.WorkspaceMemberships.AddRange(
        new WorkspaceMembership(
            primaryWorkspaceId,
            primaryUserId,
            WorkspaceRole.Owner,
            now),
        new WorkspaceMembership(
            foreignWorkspaceId,
            foreignUserId,
            WorkspaceRole.Owner,
            now));

    foreach (var item in dataset.Documents)
    {
        var isForeign = string.Equals(
            item.WorkspaceFixture,
            "foreign",
            StringComparison.OrdinalIgnoreCase);
        var workspaceId = isForeign
            ? foreignWorkspaceId
            : primaryWorkspaceId;
        var userId = isForeign
            ? foreignUserId
            : primaryUserId;

        var document = new KnowledgeDocument(
            Guid.NewGuid(),
            workspaceId,
            userId,
            item.Title,
            item.SourceName,
            item.Content,
            now);

        var chunkTexts = chunker.Chunk(item.Content);
        if (chunkTexts.Count == 0)
            throw new InvalidOperationException(
                $"Dataset document '{item.Id}' produced no chunks.");

        var chunks = chunkTexts
            .Select((content, ordinal) =>
                new KnowledgeChunk(
                    Guid.NewGuid(),
                    document.Id,
                    workspaceId,
                    ordinal,
                    content,
                    now))
            .ToArray();

        db.KnowledgeDocuments.Add(document);
        db.KnowledgeChunks.AddRange(chunks);
        document.MarkReady(chunks.Length, now);
    }

    await db.SaveChangesAsync();
    return new SeedResult(primaryWorkspaceId, foreignWorkspaceId);
}

static CorpusStats ComputeCorpusStats(
    LiveDataset dataset,
    IKnowledgeChunker chunker)
{
    var primaryChunks = 0;
    var foreignChunks = 0;

    foreach (var item in dataset.Documents)
    {
        var count = chunker.Chunk(item.Content).Count;
        if (string.Equals(
                item.WorkspaceFixture,
                "foreign",
                StringComparison.OrdinalIgnoreCase))
            foreignChunks += count;
        else
            primaryChunks += count;
    }

    static int Calls(int count) =>
        count == 0 ? 0 : (int)Math.Ceiling(count / 64d);

    return new CorpusStats(
        primaryChunks,
        foreignChunks,
        Calls(primaryChunks) + Calls(foreignChunks));
}

static async Task<LiveDataset> LoadAndVerifyDatasetAsync(
    string datasetPath,
    string hashPath)
{
    var bytes = await File.ReadAllBytesAsync(datasetPath);
    var actualHash = Convert.ToHexString(
            SHA256.HashData(bytes))
        .ToLowerInvariant();
    var expectedHash = (await File.ReadAllTextAsync(hashPath)).Trim();

    if (!string.Equals(
            actualHash,
            expectedHash,
            StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            "RAG evaluation dataset hash does not match dataset.sha256.");

    var dataset = JsonSerializer.Deserialize<LiveDataset>(
                      bytes,
                      new JsonSerializerOptions
                      {
                          PropertyNameCaseInsensitive = true
                      })
                  ?? throw new InvalidOperationException(
                      "Could not deserialize RAG evaluation dataset.");

    if (dataset.Version != "rag-v2" ||
        dataset.Documents.Length == 0 ||
        dataset.Cases.Length == 0)
        throw new InvalidOperationException(
            "Unexpected or empty RAG evaluation dataset.");

    return dataset with { Sha256 = actualHash };
}

static string FindRepoRoot()
{
    var starts = new[]
    {
        Directory.GetCurrentDirectory(),
        AppContext.BaseDirectory
    };

    foreach (var start in starts)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "evals",
                    "rag",
                    "v2",
                    "dataset.json")))
                return directory.FullName;

            directory = directory.Parent;
        }
    }

    throw new DirectoryNotFoundException(
        "Could not locate the ICEHOTT repository root.");
}

static decimal ParseRequiredDecimal(string name)
{
    var raw = Environment.GetEnvironmentVariable(name);
    if (!decimal.TryParse(
            raw,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var value) ||
        value <= 0)
        throw new InvalidOperationException(
            $"{name} must be configured as a positive decimal value.");

    return value;
}

static int ParseOptionalInt(
    string name,
    int fallback,
    int min,
    int max)
{
    var raw = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(raw))
        return fallback;

    if (!int.TryParse(
            raw,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value))
        throw new InvalidOperationException(
            $"{name} must be an integer.");

    return Math.Clamp(value, min, max);
}

static async Task WriteFailureAsync(
    string artifactPath,
    Guid runId,
    LiveDataset dataset,
    string errorType,
    string errorMessage) =>
    await WriteJsonAsync(
        artifactPath,
        new
        {
            success = false,
            runId,
            completedAtUtc = DateTimeOffset.UtcNow,
            dataset = new
            {
                version = dataset.Version,
                sha256 = dataset.Sha256
            },
            failure = new
            {
                type = errorType,
                message = errorMessage
            },
            activationPerformed = false
        });

static async Task WriteJsonAsync(
    string path,
    object value)
{
    await File.WriteAllTextAsync(
        path,
        JsonSerializer.Serialize(value, JsonOptions()));
}

static JsonSerializerOptions JsonOptions() =>
    new()
    {
        WriteIndented = true
    };

static void PrintUsage()
{
    Console.WriteLine(
        """
        ICEHOTT Phase 3.6B live provider benchmark

        Dry-run (no network/provider cost):
          dotnet run --project backend/tools/ICEHOTT.Benchmarks -- --dry-run

        Live (paid provider calls; explicit opt-in required):
          dotnet run --project backend/tools/ICEHOTT.Benchmarks -c Release -- --live

        Required live environment:
          ICEHOTT_BENCHMARK_CONNECTION=<dedicated fresh PostgreSQL database containing 'benchmark' in its name>
          ICEHOTT_LIVE_PROVIDER_BENCHMARK_OPT_IN=I_UNDERSTAND_THIS_USES_PAID_API
          OPENAI_API_KEY=<server-side secret>  (or ICEHOTT_OPENAI_API_KEY)
          ICEHOTT_OPENAI_EMBEDDING_USD_PER_MILLION_INPUT_TOKENS=<approved pricing metadata>
          ICEHOTT_LIVE_BENCHMARK_MAX_COST_USD=<approved maximum benchmark cost>

        The harness never activates the candidate profile.
        """);
}

internal sealed record LiveDataset(
    string Version,
    string Description,
    LiveDocument[] Documents,
    LiveCase[] Cases,
    LiveThresholds Thresholds,
    string Sha256 = "");

internal sealed record LiveDocument(
    string Id,
    string Title,
    string SourceName,
    string WorkspaceFixture,
    string Content);

internal sealed record LiveCase(
    string Id,
    string Category,
    string[] Tags,
    string Query,
    string[] ExpectedSources,
    string[] ForbiddenSources,
    string[] TenantForbiddenSources,
    int TopK);

internal sealed record LiveThresholds(
    double HitRateAtK,
    double MeanRecallAtK,
    double MeanPrecisionAtK,
    double CitationCorrectness,
    int TenantLeakageCount);

internal sealed record SeedResult(
    Guid PrimaryWorkspaceId,
    Guid ForeignWorkspaceId);

internal sealed record CorpusStats(
    int PrimaryChunks,
    int ForeignChunks,
    int ExpectedDocumentEmbeddingCalls);
