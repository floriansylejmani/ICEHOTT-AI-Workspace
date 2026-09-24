using System.Net.Http.Json;
using ICEHOTT.Application.Abstractions;

namespace ICEHOTT.Infrastructure.Ai;

public sealed class AiRuntimeClient(HttpClient httpClient) : IAiRuntimeClient
{
    public async Task<AiRuntimeReply> ReplyAsync(
        AiRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new RuntimeRequest(
                request.WorkspaceId,
                request.UserId,
                request.ConversationId,
                request.Messages.Select(x => new RuntimeMessage(x.Role, x.Content)).ToArray(),
                request.Knowledge.Select(x => new RuntimeKnowledge(
                    x.ChunkId,
                    x.DocumentId,
                    x.Title,
                    x.SourceName,
                    x.Content,
                    x.Score)).ToArray());

            using var response = await httpClient.PostAsJsonAsync("v1/chat", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new AiRuntimeUnavailableException(
                    $"AI runtime returned HTTP {(int)response.StatusCode}.");

            var result = await response.Content.ReadFromJsonAsync<RuntimeResponse>(
                cancellationToken: cancellationToken);

            if (result is null || string.IsNullOrWhiteSpace(result.Content))
                throw new AiRuntimeUnavailableException("AI runtime returned an empty response.");

            return new AiRuntimeReply(result.Content, result.Provider, result.Model);
        }
        catch (AiRuntimeUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            throw new AiRuntimeUnavailableException("AI runtime is unavailable.", exception);
        }
    }

    public async Task<AiEmbeddingReply> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "v1/embeddings",
                new EmbeddingRequest(texts),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new AiRuntimeUnavailableException(
                    $"Embedding runtime returned HTTP {(int)response.StatusCode}.");

            var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(
                cancellationToken: cancellationToken);

            if (result is null || result.Embeddings.Count != texts.Count)
                throw new AiRuntimeUnavailableException("Embedding runtime returned an invalid response.");

            return new AiEmbeddingReply(
                result.Dimensions,
                result.Embeddings.Select(vector => (IReadOnlyList<float>)vector).ToArray());
        }
        catch (AiRuntimeUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            throw new AiRuntimeUnavailableException("Embedding runtime is unavailable.", exception);
        }
    }

    private sealed record RuntimeRequest(
        Guid WorkspaceId,
        Guid UserId,
        Guid ConversationId,
        IReadOnlyList<RuntimeMessage> Messages,
        IReadOnlyList<RuntimeKnowledge> Knowledge);

    private sealed record RuntimeMessage(string Role, string Content);

    private sealed record RuntimeKnowledge(
        Guid ChunkId,
        Guid DocumentId,
        string Title,
        string? SourceName,
        string Content,
        double Score);

    private sealed record RuntimeResponse(string Content, string Provider, string Model);

    private sealed record EmbeddingRequest(IReadOnlyList<string> Texts);

    private sealed record EmbeddingResponse(int Dimensions, List<List<float>> Embeddings);
}
