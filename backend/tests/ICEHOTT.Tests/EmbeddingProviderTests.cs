using ICEHOTT.Application.Abstractions;
using ICEHOTT.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace ICEHOTT.Tests;

public sealed class EmbeddingProviderTests
{
    [Fact]
    public async Task Runtime_Dimension_Mismatch_Is_NonRetryable()
    {
        var options = Options.Create(new AiRuntimeOptions
        {
            EmbeddingProfileId = Guid.NewGuid(),
            EmbeddingProfileKey = "test-32-v1",
            EmbeddingProvider = "test-runtime",
            EmbeddingModel = "test-32",
            EmbeddingDimensions = 32,
            EmbeddingVersion = "1",
            EmbeddingIndexVersion = 1,
            EmbeddingDistanceMetric = "cosine",
            EmbeddingNormalization = "unit"
        });

        var provider = new AiRuntimeEmbeddingProvider(
            new FixedDimensionRuntime(64),
            options);

        var exception = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => provider.EmbedAsync(["hello"]));

        Assert.Equal(EmbeddingFailureKind.ProfileMismatch, exception.Kind);
        Assert.False(exception.IsRetryable);
    }

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

            return Task.FromResult(
                new AiEmbeddingReply(dimensions, vectors));
        }

        public Task<bool> IsReadyAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
