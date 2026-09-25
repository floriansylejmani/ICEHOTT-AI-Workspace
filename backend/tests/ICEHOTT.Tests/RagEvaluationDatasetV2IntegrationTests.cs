using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ICEHOTT.Application.Knowledge;

namespace ICEHOTT.Tests;

public sealed class RagEvaluationDatasetV2IntegrationTests : IClassFixture<IcehottApiFactory>
{
    private readonly IcehottApiFactory _factory;

    public RagEvaluationDatasetV2IntegrationTests(IcehottApiFactory factory) =>
        _factory = factory;

    [Fact]
    public async Task Expanded_Rag_V2_Dataset_Is_Frozen_And_Meets_Deterministic_Gates()
    {
        var dataset = await LoadDatasetAsync();
        AssertDatasetIntegrity(dataset);
        await AssertDatasetHashAsync();

        using var client = _factory.CreateClient();
        var token = await RegisterAsync(
            client,
            $"rag-v2-{Guid.NewGuid():N}@icehott.dev");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var primaryWorkspaceId = await CreateWorkspaceAsync(
            client,
            $"RAG Eval {dataset.Version} Primary");
        var foreignWorkspaceId = await CreateWorkspaceAsync(
            client,
            $"RAG Eval {dataset.Version} Foreign");

        foreach (var document in dataset.Documents)
        {
            var workspaceId = document.WorkspaceFixture.Equals(
                "foreign",
                StringComparison.OrdinalIgnoreCase)
                ? foreignWorkspaceId
                : primaryWorkspaceId;

            var ingest = await client.PostAsJsonAsync(
                $"/api/workspaces/{workspaceId}/knowledge/documents",
                new
                {
                    title = document.Title,
                    sourceName = document.SourceName,
                    content = document.Content
                });

            Assert.Equal(HttpStatusCode.Accepted, ingest.StatusCode);
        }

        for (var index = 0; index < dataset.Documents.Length; index++)
            Assert.True(await _factory.ProcessNextKnowledgeJobAsync());

        await AssertWorkspaceDocumentCountAsync(
            client,
            primaryWorkspaceId,
            dataset.Documents.Count(x =>
                x.WorkspaceFixture.Equals(
                    "primary",
                    StringComparison.OrdinalIgnoreCase)));

        await AssertWorkspaceDocumentCountAsync(
            client,
            foreignWorkspaceId,
            dataset.Documents.Count(x =>
                x.WorkspaceFixture.Equals(
                    "foreign",
                    StringComparison.OrdinalIgnoreCase)));

        var observations = new List<RagEvaluationObservation>();

        foreach (var evaluationCase in dataset.Cases)
        {
            var search = await client.GetAsync(
                $"/api/workspaces/{primaryWorkspaceId}/knowledge/search?query=" +
                Uri.EscapeDataString(evaluationCase.Query) +
                $"&limit={evaluationCase.TopK}");
            search.EnsureSuccessStatusCode();

            using var searchJson = JsonDocument.Parse(
                await search.Content.ReadAsStringAsync());

            var retrievedSources = ReadSourceNames(searchJson.RootElement);

            AssertNoBlockedSources(
                retrievedSources,
                evaluationCase.ForbiddenSources,
                evaluationCase.TenantForbiddenSources);

            var chat = await client.PostAsJsonAsync(
                $"/api/workspaces/{primaryWorkspaceId}/conversations/chat",
                new
                {
                    conversationId = (Guid?)null,
                    content = evaluationCase.Query
                });
            chat.EnsureSuccessStatusCode();

            using var chatJson = JsonDocument.Parse(
                await chat.Content.ReadAsStringAsync());

            var citations = ReadSourceNames(
                chatJson.RootElement
                    .GetProperty("assistantMessage")
                    .GetProperty("citations"));

            AssertNoBlockedSources(
                citations,
                evaluationCase.ForbiddenSources,
                evaluationCase.TenantForbiddenSources);

            var tenantLeakage =
                evaluationCase.TenantForbiddenSources.Any(source =>
                    retrievedSources.Contains(
                        source,
                        StringComparer.OrdinalIgnoreCase) ||
                    citations.Contains(
                        source,
                        StringComparer.OrdinalIgnoreCase));

            var citationCorrect =
                evaluationCase.ExpectedSources.All(expected =>
                    citations.Contains(
                        expected,
                        StringComparer.OrdinalIgnoreCase)) &&
                !evaluationCase.ForbiddenSources.Any(forbidden =>
                    citations.Contains(
                        forbidden,
                        StringComparer.OrdinalIgnoreCase)) &&
                !tenantLeakage;

            observations.Add(new RagEvaluationObservation(
                evaluationCase.Id,
                evaluationCase.ExpectedSources,
                retrievedSources,
                citationCorrect,
                tenantLeakage,
                IsNegativeSafetyControl:
                    string.Equals(
                        evaluationCase.Category,
                        "safety",
                        StringComparison.OrdinalIgnoreCase)));
        }

        var misses = observations
            .Where(x =>
                (x.ExpectedSources.Count == 0 && x.RetrievedSources.Count > 0) ||
                (x.ExpectedSources.Count > 0 &&
                 !x.RetrievedSources.Any(source =>
                     x.ExpectedSources.Contains(
                         source,
                         StringComparer.OrdinalIgnoreCase))))
            .Select(x => $"{x.CaseId} => [{string.Join(", ", x.RetrievedSources)}]")
            .ToArray();

        var summary = RagEvaluationMetrics.Summarize(observations);

        Assert.True(
            summary.HitRateAtK >= dataset.Thresholds.HitRateAtK,
            $"HitRate@K {summary.HitRateAtK:F3} < {dataset.Thresholds.HitRateAtK:F3}; misses: {string.Join("; ", misses)}");
        Assert.True(
            summary.MeanRecallAtK >= dataset.Thresholds.MeanRecallAtK,
            $"Recall@K {summary.MeanRecallAtK:F3} < {dataset.Thresholds.MeanRecallAtK:F3}");
        Assert.True(
            summary.MeanPrecisionAtK >= dataset.Thresholds.MeanPrecisionAtK,
            $"Precision@K {summary.MeanPrecisionAtK:F3} < {dataset.Thresholds.MeanPrecisionAtK:F3}");
        Assert.True(
            summary.CitationCorrectness >= dataset.Thresholds.CitationCorrectness,
            $"Citation correctness {summary.CitationCorrectness:F3} < {dataset.Thresholds.CitationCorrectness:F3}");
        Assert.Equal(
            dataset.Thresholds.TenantLeakageCount,
            summary.TenantLeakageCount);
    }

    private static void AssertDatasetIntegrity(RagEvalDataset dataset)
    {
        Assert.Equal("rag-v2", dataset.Version);
        Assert.True(dataset.Documents.Length >= 12);
        Assert.True(dataset.Cases.Length >= 16);

        Assert.Equal(
            dataset.Documents.Length,
            dataset.Documents.Select(x => x.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.Equal(
            dataset.Documents.Length,
            dataset.Documents.Select(x => x.SourceName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.Equal(
            dataset.Cases.Length,
            dataset.Cases.Select(x => x.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());

        var sourceNames = dataset.Documents
            .Select(x => x.SourceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(
            dataset.Documents,
            x => x.WorkspaceFixture.Equals(
                "foreign",
                StringComparison.OrdinalIgnoreCase));

        foreach (var document in dataset.Documents)
        {
            Assert.Contains(
                document.WorkspaceFixture,
                new[] { "primary", "foreign" });
            Assert.False(string.IsNullOrWhiteSpace(document.Content));
        }

        foreach (var evaluationCase in dataset.Cases)
        {
            Assert.False(string.IsNullOrWhiteSpace(evaluationCase.Category));
            Assert.NotEmpty(evaluationCase.Tags);
            Assert.InRange(evaluationCase.TopK, 1, 10);
            Assert.All(
                evaluationCase.ExpectedSources,
                source => Assert.Contains(source, sourceNames));
            Assert.All(
                evaluationCase.ForbiddenSources,
                source => Assert.Contains(source, sourceNames));
            Assert.All(
                evaluationCase.TenantForbiddenSources,
                source => Assert.Contains(source, sourceNames));
            Assert.Empty(
                evaluationCase.ExpectedSources.Intersect(
                    evaluationCase.ForbiddenSources,
                    StringComparer.OrdinalIgnoreCase));
        }

        Assert.InRange(dataset.Thresholds.HitRateAtK, 0, 1);
        Assert.InRange(dataset.Thresholds.MeanRecallAtK, 0, 1);
        Assert.InRange(dataset.Thresholds.MeanPrecisionAtK, 0, 1);
        Assert.InRange(dataset.Thresholds.CitationCorrectness, 0, 1);
        Assert.Equal(0, dataset.Thresholds.TenantLeakageCount);
    }

    private static async Task AssertDatasetHashAsync()
    {
        var datasetPath = DatasetPath("dataset.json");
        var lockPath = DatasetPath("dataset.sha256");
        var expected = (await File.ReadAllTextAsync(lockPath)).Trim();
        var actual = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(datasetPath)))
            .ToLowerInvariant();

        Assert.Equal(expected, actual);
    }

    private static string[] ReadSourceNames(JsonElement array) =>
        array.EnumerateArray()
            .Select(item =>
                item.TryGetProperty("sourceName", out var source) &&
                source.ValueKind == JsonValueKind.String
                    ? source.GetString()
                    : null)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Cast<string>()
            .ToArray();

    private static void AssertNoBlockedSources(
        IReadOnlyCollection<string> actual,
        IReadOnlyCollection<string> forbidden,
        IReadOnlyCollection<string> tenantForbidden)
    {
        Assert.DoesNotContain(
            actual,
            source => forbidden.Contains(
                source,
                StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            actual,
            source => tenantForbidden.Contains(
                source,
                StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<Guid> CreateWorkspaceAsync(
        HttpClient client,
        string name)
    {
        var response = await client.PostAsJsonAsync(
            "/api/workspaces",
            new { name });
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task AssertWorkspaceDocumentCountAsync(
        HttpClient client,
        Guid workspaceId,
        int expectedCount)
    {
        var response = await client.GetAsync(
            $"/api/workspaces/{workspaceId}/knowledge/documents");
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCount, json.RootElement.GetArrayLength());
        Assert.All(
            json.RootElement.EnumerateArray(),
            item => Assert.Equal(
                "Ready",
                item.GetProperty("status").GetString()));
    }

    private static async Task<RagEvalDataset> LoadDatasetAsync()
    {
        await using var stream = File.OpenRead(DatasetPath("dataset.json"));
        return await JsonSerializer.DeserializeAsync<RagEvalDataset>(
                   stream,
                   new JsonSerializerOptions
                   {
                       PropertyNameCaseInsensitive = true
                   })
               ?? throw new InvalidOperationException(
                   "RAG v2 evaluation dataset is invalid.");
    }

    private static string DatasetPath(string fileName) =>
        Path.Combine(
            AppContext.BaseDirectory,
            "evals",
            "rag",
            "v2",
            fileName);

    private static async Task<string> RegisterAsync(
        HttpClient client,
        string email)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                displayName = "RAG V2 Evaluation User",
                password = "StrongPassword123!"
            });

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private sealed record RagEvalDataset(
        string Version,
        string Description,
        RagEvalDocument[] Documents,
        RagEvalCase[] Cases,
        RagEvalThresholds Thresholds);

    private sealed record RagEvalDocument(
        string Id,
        string Title,
        string SourceName,
        string WorkspaceFixture,
        string Content);

    private sealed record RagEvalCase(
        string Id,
        string Category,
        string[] Tags,
        string Query,
        string[] ExpectedSources,
        string[] ForbiddenSources,
        string[] TenantForbiddenSources,
        int TopK);

    private sealed record RagEvalThresholds(
        double HitRateAtK,
        double MeanRecallAtK,
        double MeanPrecisionAtK,
        double CitationCorrectness,
        int TenantLeakageCount);
}
