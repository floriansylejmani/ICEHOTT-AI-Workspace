using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ICEHOTT.Application.Knowledge;

namespace ICEHOTT.Tests;

public sealed class RagEvaluationDatasetIntegrationTests : IClassFixture<IcehottApiFactory>
{
    private readonly IcehottApiFactory _factory;

    public RagEvaluationDatasetIntegrationTests(IcehottApiFactory factory) =>
        _factory = factory;

    [Fact]
    public async Task Versioned_Rag_Dataset_Meets_Deterministic_Gates()
    {
        var dataset = await LoadDatasetAsync();
        Assert.Equal("rag-v1", dataset.Version);
        Assert.NotEmpty(dataset.Documents);
        Assert.NotEmpty(dataset.Cases);
        Assert.Equal(
            dataset.Documents.Length,
            dataset.Documents.Select(x => x.SourceName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            dataset.Cases.Length,
            dataset.Cases.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        using var client = _factory.CreateClient();
        var token = await RegisterAsync(
            client,
            $"rag-eval-{Guid.NewGuid():N}@icehott.dev");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var workspaceResponse = await client.PostAsJsonAsync(
            "/api/workspaces",
            new { name = $"RAG Eval {dataset.Version}" });
        workspaceResponse.EnsureSuccessStatusCode();

        var workspaceId = JsonDocument.Parse(
            await workspaceResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        foreach (var document in dataset.Documents)
        {
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

        var listed = await client.GetAsync(
            $"/api/workspaces/{workspaceId}/knowledge/documents");
        listed.EnsureSuccessStatusCode();

        using (var listedJson = JsonDocument.Parse(
                   await listed.Content.ReadAsStringAsync()))
        {
            Assert.Equal(dataset.Documents.Length, listedJson.RootElement.GetArrayLength());
            Assert.All(
                listedJson.RootElement.EnumerateArray(),
                item => Assert.Equal("Ready", item.GetProperty("status").GetString()));
        }

        var observations = new List<RagEvaluationObservation>();

        foreach (var evaluationCase in dataset.Cases)
        {
            var search = await client.GetAsync(
                $"/api/workspaces/{workspaceId}/knowledge/search?query=" +
                Uri.EscapeDataString(evaluationCase.Query) +
                $"&limit={evaluationCase.TopK}");
            search.EnsureSuccessStatusCode();

            using var searchJson = JsonDocument.Parse(
                await search.Content.ReadAsStringAsync());

            var retrievedSources = searchJson.RootElement
                .EnumerateArray()
                .Select(item =>
                    item.TryGetProperty("sourceName", out var source) &&
                    source.ValueKind == JsonValueKind.String
                        ? source.GetString()
                        : null)
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Cast<string>()
                .ToArray();

            Assert.DoesNotContain(
                retrievedSources,
                source => evaluationCase.ForbiddenSources.Contains(
                    source,
                    StringComparer.OrdinalIgnoreCase));

            var chat = await client.PostAsJsonAsync(
                $"/api/workspaces/{workspaceId}/conversations/chat",
                new
                {
                    conversationId = (Guid?)null,
                    content = evaluationCase.Query
                });
            chat.EnsureSuccessStatusCode();

            using var chatJson = JsonDocument.Parse(
                await chat.Content.ReadAsStringAsync());

            var citations = chatJson.RootElement
                .GetProperty("assistantMessage")
                .GetProperty("citations")
                .EnumerateArray()
                .Select(item =>
                    item.TryGetProperty("sourceName", out var source) &&
                    source.ValueKind == JsonValueKind.String
                        ? source.GetString()
                        : null)
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Cast<string>()
                .ToArray();

            var citationCorrect =
                evaluationCase.ExpectedSources.All(expected =>
                    citations.Contains(expected, StringComparer.OrdinalIgnoreCase)) &&
                !evaluationCase.ForbiddenSources.Any(forbidden =>
                    citations.Contains(forbidden, StringComparer.OrdinalIgnoreCase));

            observations.Add(new RagEvaluationObservation(
                evaluationCase.Id,
                evaluationCase.ExpectedSources,
                retrievedSources,
                citationCorrect,
                TenantLeakage: false));
        }

        var summary = RagEvaluationMetrics.Summarize(observations);

        Assert.True(
            summary.HitRateAtK >= dataset.Thresholds.HitRateAtK,
            $"HitRate@K {summary.HitRateAtK:F3} < {dataset.Thresholds.HitRateAtK:F3}");
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

    private static async Task<RagEvalDataset> LoadDatasetAsync()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "evals",
            "rag",
            "v1",
            "dataset.json");

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RagEvalDataset>(
                   stream,
                   new JsonSerializerOptions
                   {
                       PropertyNameCaseInsensitive = true
                   })
               ?? throw new InvalidOperationException("RAG evaluation dataset is invalid.");
    }

    private static async Task<string> RegisterAsync(
        HttpClient client,
        string email)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                displayName = "RAG Evaluation User",
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
        string Content);

    private sealed record RagEvalCase(
        string Id,
        string Query,
        string[] ExpectedSources,
        string[] ForbiddenSources,
        int TopK);

    private sealed record RagEvalThresholds(
        double HitRateAtK,
        double MeanRecallAtK,
        double MeanPrecisionAtK,
        double CitationCorrectness,
        int TenantLeakageCount);
}
