using ICEHOTT.Application.Abstractions;
using ICEHOTT.Infrastructure.Ai;

namespace ICEHOTT.Tests;

public sealed class EmbeddingProviderTests
{
    private static EmbeddingProfileDescriptor ProfileFor(int dimensions) =>
        new(Guid.NewGuid(),
            $"test-{dimensions}d-v1",
            "icehott-ai-runtime",
            "deterministic-64d",
            dimensions,
            "1",
            1,
            "cosine",
            "unit");

    // ── Provider name mismatch ──────────────────────────────────────────────────

    [Fact]
    public async Task Provider_Name_Mismatch_Is_NonRetryable()
    {
        var provider = new AiRuntimeEmbeddingProvider(new FixedDimensionRuntime(64));

        var wrongProviderProfile = new EmbeddingProfileDescriptor(
            Guid.NewGuid(), "openai-test", "openai", "text-embedding-3-small",
            1536, "1", 1, "cosine", "unit");

        var exception = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => provider.EmbedAsync(wrongProviderProfile, EmbeddingPurpose.Document, ["hello"]));

        Assert.Equal(EmbeddingFailureKind.ProfileMismatch, exception.Kind);
        Assert.False(exception.IsRetryable);
        Assert.Contains("openai", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Unsupported dimensions ──────────────────────────────────────────────────

    [Fact]
    public async Task Unsupported_Dimensions_Is_NonRetryable()
    {
        var provider = new AiRuntimeEmbeddingProvider(new FixedDimensionRuntime(64));

        // icehott-ai-runtime only supports 64 dimensions
        var profile = ProfileFor(1536);

        var exception = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => provider.EmbedAsync(profile, EmbeddingPurpose.Document, ["hello"]));

        Assert.Equal(EmbeddingFailureKind.ProfileMismatch, exception.Kind);
        Assert.False(exception.IsRetryable);
    }

    [Fact]
    public async Task Local_Provider_Rejects_Model_Mismatch()
    {
        var provider = new AiRuntimeEmbeddingProvider(
            new FixedDimensionRuntime(64));
        var profile = EmbeddingProfileDefaults.LocalDeterministic64 with
        {
            Model = "wrong-model"
        };

        var exception = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => provider.EmbedAsync(
                profile,
                EmbeddingPurpose.Query,
                ["hello"]));

        Assert.Equal(EmbeddingFailureKind.ProfileMismatch, exception.Kind);
        Assert.False(exception.IsRetryable);
        Assert.False(provider.IsConfigured(profile));
        Assert.True(provider.IsConfigured(
            EmbeddingProfileDefaults.LocalDeterministic64));
    }

    // ── Runtime dimension mismatch ──────────────────────────────────────────────

    [Fact]
    public async Task Runtime_Dimension_Mismatch_Is_NonRetryable()
    {
        // Provider says it supports 64 dims; runtime returns only 32 dims.
        // Profile requests 64 dims (passes capabilities check) but runtime disagrees.
        var provider = new AiRuntimeEmbeddingProvider(new FixedDimensionRuntime(32));

        // Override SupportedDimensions via a subclass is not possible with sealed; instead
        // we test the path where the runtime disagrees with what the profile expects.
        // Since SupportedDimensions = {64} and profile.Dimensions must be 64, the
        // runtime's 32-dim reply triggers the post-call dimension check.
        var profile = ProfileFor(64);

        var exception = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => provider.EmbedAsync(profile, EmbeddingPurpose.Document, ["hello"]));

        Assert.Equal(EmbeddingFailureKind.ProfileMismatch, exception.Kind);
        Assert.False(exception.IsRetryable);
    }

    // ── Happy path ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Provider_Returns_Batch_With_Correct_Profile_And_Dimensions()
    {
        var provider = new AiRuntimeEmbeddingProvider(new FixedDimensionRuntime(64));
        var profile = ProfileFor(64);

        var batch = await provider.EmbedAsync(
            profile, EmbeddingPurpose.Document, ["alpha", "beta", "gamma"]);

        Assert.Equal(profile, batch.Profile);
        Assert.Equal(3, batch.Embeddings.Count);
        Assert.All(batch.Embeddings, v => Assert.Equal(64, v.Count));
    }

    [Fact]
    public async Task Provider_Returns_Empty_Batch_For_Empty_Input()
    {
        var provider = new AiRuntimeEmbeddingProvider(new FixedDimensionRuntime(64));
        var profile = ProfileFor(64);

        var batch = await provider.EmbedAsync(profile, EmbeddingPurpose.Query, []);

        Assert.Equal(profile, batch.Profile);
        Assert.Empty(batch.Embeddings);
    }

    // ── Capabilities ────────────────────────────────────────────────────────────

    [Fact]
    public void Provider_Capabilities_Are_Correct_For_AiRuntime()
    {
        var provider = new AiRuntimeEmbeddingProvider(new FixedDimensionRuntime(64));

        Assert.Equal("icehott-ai-runtime", provider.Provider);
        Assert.Contains(64, provider.Capabilities.SupportedDimensions);
        Assert.Equal(64, provider.Capabilities.MaxBatchInputs);
        Assert.Null(provider.Capabilities.MaxInputTokens);
        Assert.False(provider.Capabilities.SupportsPurposeRouting);
    }

    // ── Transient error passthrough ─────────────────────────────────────────────

    [Fact]
    public async Task Transient_Runtime_Error_Is_Retryable()
    {
        var provider = new AiRuntimeEmbeddingProvider(new UnavailableRuntime());
        var profile = ProfileFor(64);

        var exception = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => provider.EmbedAsync(profile, EmbeddingPurpose.Document, ["hello"]));

        Assert.Equal(EmbeddingFailureKind.Transient, exception.Kind);
        Assert.True(exception.IsRetryable);
    }

    // ── Test doubles ────────────────────────────────────────────────────────────

    private sealed class FixedDimensionRuntime(int dimensions) : IAiRuntimeClient
    {
        public Task<AiRuntimeReply> ReplyAsync(
            AiRuntimeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiRuntimeReply("unused", "test", "test"));

        public Task<AiEmbeddingReply> EmbedAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<IReadOnlyList<float>> vectors = texts
                .Select(_ => (IReadOnlyList<float>)new float[dimensions])
                .ToArray();

            return Task.FromResult(new AiEmbeddingReply(dimensions, vectors));
        }

        public Task<bool> IsReadyAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class UnavailableRuntime : IAiRuntimeClient
    {
        public Task<AiRuntimeReply> ReplyAsync(
            AiRuntimeRequest request,
            CancellationToken cancellationToken = default) =>
            throw new AiRuntimeUnavailableException("test unavailable");

        public Task<AiEmbeddingReply> EmbedAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default) =>
            throw new AiRuntimeUnavailableException("test unavailable");

        public Task<bool> IsReadyAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
