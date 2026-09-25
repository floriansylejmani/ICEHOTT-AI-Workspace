using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ICEHOTT.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace ICEHOTT.Infrastructure.Ai;

public sealed class OpenAiEmbeddingProvider(
    HttpClient httpClient,
    IOptions<OpenAiEmbeddingOptions> options) :
    IEmbeddingProvider,
    IEmbeddingProviderConfigurationProbe
{
    private const string ProviderName = "openai";
    private const string SupportedModel = "text-embedding-3-small";
    private static readonly IReadOnlySet<int> SupportedDims =
        new HashSet<int> { 1536 };

    private readonly OpenAiEmbeddingOptions _options = options.Value;

    public string Provider => ProviderName;

    public EmbeddingProviderCapabilities Capabilities { get; } = new(
        ProviderName,
        SupportedDims,
        MaxBatchInputs: 64,
        MaxInputTokens: null,
        SupportsPurposeRouting: false);

    public bool IsConfigured(EmbeddingProfileDescriptor profile)
    {
        try
        {
            ValidateProfile(profile);
            return !string.IsNullOrWhiteSpace(_options.ApiKey);
        }
        catch
        {
            return false;
        }
    }

    public async Task<EmbeddingBatch> EmbedAsync(
        EmbeddingProfileDescriptor profile,
        EmbeddingPurpose purpose,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profile);

        if (texts.Count == 0)
            return new EmbeddingBatch(profile, []);

        if (texts.Count > Capabilities.MaxBatchInputs)
            throw new EmbeddingProviderException(
                $"OpenAI embedding batch exceeds the configured cap of {Capabilities.MaxBatchInputs} inputs.",
                EmbeddingFailureKind.InvalidInput);

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new EmbeddingProviderException(
                "OpenAI embedding provider is not configured with a server-side API key.",
                EmbeddingFailureKind.Configuration);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "embeddings");

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        request.Content = JsonContent.Create(new OpenAiEmbeddingRequest(
            texts,
            profile.Model,
            "float",
            profile.Dimensions));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (TaskCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmbeddingProviderException(
                "OpenAI embedding request timed out.",
                EmbeddingFailureKind.Transient,
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new EmbeddingProviderException(
                "OpenAI embedding request failed at the network layer.",
                EmbeddingFailureKind.Transient,
                exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw CreateHttpFailure(response.StatusCode);

            OpenAiEmbeddingResponse? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<OpenAiEmbeddingResponse>(
                    cancellationToken: cancellationToken);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                throw new EmbeddingProviderException(
                    "OpenAI embedding response could not be parsed.",
                    EmbeddingFailureKind.Permanent,
                    exception);
            }

            if (payload is null ||
                payload.Data is null ||
                payload.Data.Count != texts.Count)
                throw new EmbeddingProviderException(
                    "OpenAI embedding response count did not match the request.",
                    EmbeddingFailureKind.ProfileMismatch);

            if (!string.IsNullOrWhiteSpace(payload.Model) &&
                !string.Equals(
                    payload.Model,
                    profile.Model,
                    StringComparison.Ordinal))
                throw new EmbeddingProviderException(
                    $"OpenAI returned model '{payload.Model}' but profile requires '{profile.Model}'.",
                    EmbeddingFailureKind.ProfileMismatch);

            var ordered = payload.Data
                .OrderBy(x => x.Index)
                .ToArray();

            for (var index = 0; index < ordered.Length; index++)
            {
                if (ordered[index].Index != index)
                    throw new EmbeddingProviderException(
                        "OpenAI embedding response indexes were not contiguous.",
                        EmbeddingFailureKind.ProfileMismatch);

                var vector = ordered[index].Embedding;
                if (vector is null ||
                    vector.Count != profile.Dimensions)
                    throw new EmbeddingProviderException(
                        $"OpenAI returned an embedding incompatible with {profile.Dimensions} dimensions.",
                        EmbeddingFailureKind.ProfileMismatch);
            }

            IReadOnlyList<IReadOnlyList<float>> embeddings =
                ordered
                    .Select(x => (IReadOnlyList<float>)x.Embedding!)
                    .ToArray();

            var usage = payload.Usage is null
                ? null
                : new EmbeddingUsage(
                    payload.Usage.PromptTokens,
                    payload.Usage.TotalTokens);

            return new EmbeddingBatch(
                profile,
                embeddings,
                usage);
        }
    }

    private static void ValidateProfile(
        EmbeddingProfileDescriptor profile)
    {
        profile.Validate();

        if (!string.Equals(
                profile.Provider,
                ProviderName,
                StringComparison.OrdinalIgnoreCase))
            throw new EmbeddingProviderException(
                $"Profile provider '{profile.Provider}' does not match OpenAI.",
                EmbeddingFailureKind.ProfileMismatch);

        if (!string.Equals(
                profile.Model,
                SupportedModel,
                StringComparison.Ordinal))
            throw new EmbeddingProviderException(
                $"OpenAI foundation adapter currently supports only '{SupportedModel}'.",
                EmbeddingFailureKind.Configuration);

        if (!SupportedDims.Contains(profile.Dimensions))
            throw new EmbeddingProviderException(
                "OpenAI foundation adapter currently supports only 1536-dimensional profiles.",
                EmbeddingFailureKind.ProfileMismatch);
    }

    private static EmbeddingProviderException CreateHttpFailure(
        HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.TooManyRequests =>
                new EmbeddingProviderException(
                    "OpenAI embedding request was rate limited.",
                    EmbeddingFailureKind.RateLimited),

            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new EmbeddingProviderException(
                    "OpenAI embedding authentication failed.",
                    EmbeddingFailureKind.Authentication),

            HttpStatusCode.BadRequest or
            HttpStatusCode.UnprocessableEntity =>
                new EmbeddingProviderException(
                    "OpenAI embedding request was rejected as invalid.",
                    EmbeddingFailureKind.InvalidInput),

            HttpStatusCode.RequestTimeout or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout =>
                new EmbeddingProviderException(
                    "OpenAI embedding service is temporarily unavailable.",
                    EmbeddingFailureKind.Transient),

            _ when (int)statusCode >= 500 =>
                new EmbeddingProviderException(
                    "OpenAI embedding service returned a server error.",
                    EmbeddingFailureKind.Transient),

            _ =>
                new EmbeddingProviderException(
                    $"OpenAI embedding request failed with HTTP {(int)statusCode}.",
                    EmbeddingFailureKind.Permanent)
        };

    private sealed record OpenAiEmbeddingRequest(
        [property: JsonPropertyName("input")]
        IReadOnlyList<string> Input,
        [property: JsonPropertyName("model")]
        string Model,
        [property: JsonPropertyName("encoding_format")]
        string EncodingFormat,
        [property: JsonPropertyName("dimensions")]
        int Dimensions);

    private sealed record OpenAiEmbeddingResponse(
        [property: JsonPropertyName("data")]
        IReadOnlyList<OpenAiEmbeddingData>? Data,
        [property: JsonPropertyName("model")]
        string? Model,
        [property: JsonPropertyName("usage")]
        OpenAiEmbeddingUsage? Usage);

    private sealed record OpenAiEmbeddingData(
        [property: JsonPropertyName("index")]
        int Index,
        [property: JsonPropertyName("embedding")]
        IReadOnlyList<float>? Embedding);

    private sealed record OpenAiEmbeddingUsage(
        [property: JsonPropertyName("prompt_tokens")]
        int? PromptTokens,
        [property: JsonPropertyName("total_tokens")]
        int? TotalTokens);
}
