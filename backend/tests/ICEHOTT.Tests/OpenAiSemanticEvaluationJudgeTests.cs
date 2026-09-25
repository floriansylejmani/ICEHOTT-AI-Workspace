using System.Net;
using System.Text;
using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace ICEHOTT.Tests;

public sealed class OpenAiSemanticEvaluationJudgeTests
{
    [Fact]
    public async Task Judge_Uses_Structured_Output_And_Returns_Usage()
    {
        var response = JsonSerializer.Serialize(new
        {
            status = "completed",
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = """
                                {"groundedness":0.91,"answer_relevance":0.92,"faithfulness":0.93,"context_precision":0.94,"context_recall":0.95}
                                """
                        }
                    }
                }
            },
            usage = new
            {
                input_tokens = 123,
                output_tokens = 17,
                total_tokens = 140
            }
        });

        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    response,
                    Encoding.UTF8,
                    "application/json")
            });

        var judge = CreateJudge(handler, "test-key-not-real");
        var result = await judge.JudgeAsync(
            Request());

        Assert.Equal(0.91, result.Groundedness, 5);
        Assert.Equal(0.92, result.AnswerRelevance, 5);
        Assert.Equal(0.93, result.Faithfulness, 5);
        Assert.Equal(0.94, result.ContextPrecision, 5);
        Assert.Equal(0.95, result.ContextRecall, 5);
        Assert.Equal(123, result.InputTokens);
        Assert.Equal(17, result.OutputTokens);

        Assert.Equal("/v1/responses", handler.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-key-not-real", handler.AuthorizationParameter);

        using var requestJson = JsonDocument.Parse(handler.RequestBody!);
        var root = requestJson.RootElement;
        Assert.Equal("gpt-6-luna", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal(
            "none",
            root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(
            "json_schema",
            root.GetProperty("text")
                .GetProperty("format")
                .GetProperty("type")
                .GetString());
        Assert.True(
            root.GetProperty("text")
                .GetProperty("format")
                .GetProperty("strict")
                .GetBoolean());
    }

    [Fact]
    public async Task Judge_Requires_Key_Before_Http_Call()
    {
        var handler = new RecordingHandler(_ =>
            throw new InvalidOperationException(
                "HTTP should not be called without a key."));

        var judge = CreateJudge(handler, "");

        var exception =
            await Assert.ThrowsAsync<SemanticEvaluationProviderException>(
                () => judge.JudgeAsync(Request()));

        Assert.False(exception.IsRetryable);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Judge_Classifies_Rate_Limit_As_Retryable()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var judge = CreateJudge(handler, "test-key-not-real");

        var exception =
            await Assert.ThrowsAsync<SemanticEvaluationProviderException>(
                () => judge.JudgeAsync(Request()));

        Assert.True(exception.IsRetryable);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Judge_Rejects_Out_Of_Range_Metric()
    {
        var response = JsonSerializer.Serialize(new
        {
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = """
                                {"groundedness":1.2,"answer_relevance":0.9,"faithfulness":0.9,"context_precision":0.9,"context_recall":0.9}
                                """
                        }
                    }
                }
            },
            usage = new
            {
                input_tokens = 10,
                output_tokens = 10,
                total_tokens = 20
            }
        });

        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    response,
                    Encoding.UTF8,
                    "application/json")
            });

        var judge = CreateJudge(handler, "test-key-not-real");

        await Assert.ThrowsAsync<SemanticEvaluationProviderException>(
            () => judge.JudgeAsync(Request()));
    }

    [Fact]
    public async Task Judge_Rejects_Prompt_Above_Input_Byte_Cap_Before_Http_Call()
    {
        var handler = new RecordingHandler(_ =>
            throw new InvalidOperationException(
                "HTTP should not be called above the input cap."));

        var judge = new OpenAiSemanticEvaluationJudge(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.openai.com/v1/")
            },
            Options.Create(new OpenAiSemanticEvaluationOptions
            {
                ApiKey = "test-key-not-real",
                Model = "gpt-6-luna",
                TimeoutSeconds = 30,
                MaxInputBytes = 1024,
                MaxOutputTokens = 256
            }));

        var request = new SemanticEvaluationRequest(
            "large-case",
            "What is the policy?",
            ["reference"],
            [
                new SemanticEvaluationContext(
                    "large.txt",
                    new string('x', 5000),
                    0.9)
            ]);

        var exception =
            await Assert.ThrowsAsync<SemanticEvaluationProviderException>(
                () => judge.JudgeAsync(request));

        Assert.False(exception.IsRetryable);
        Assert.Equal(0, handler.CallCount);
    }

    private static OpenAiSemanticEvaluationJudge CreateJudge(
        HttpMessageHandler handler,
        string apiKey) =>
        new(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.openai.com/v1/")
            },
            Options.Create(new OpenAiSemanticEvaluationOptions
            {
                ApiKey = apiKey,
                Model = "gpt-6-luna",
                TimeoutSeconds = 30,
                MaxOutputTokens = 256
            }));

    private static SemanticEvaluationRequest Request() =>
        new(
            "refund-window",
            "How long is the refund window?",
            ["Refunds are accepted for thirty days."],
            [
                new SemanticEvaluationContext(
                    "refunds.txt",
                    "Refunds are accepted for thirty days.",
                    0.98)
            ]);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
