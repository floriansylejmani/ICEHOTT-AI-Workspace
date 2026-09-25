namespace ICEHOTT.Application.Abstractions;

public sealed record SemanticEvaluationContext(
    string SourceName,
    string Content,
    double RetrievalScore);

public sealed record SemanticEvaluationRequest(
    string CaseId,
    string Query,
    IReadOnlyList<string> ReferenceFacts,
    IReadOnlyList<SemanticEvaluationContext> RetrievedContexts);

public sealed record SemanticEvaluationScore(
    double Groundedness,
    double AnswerRelevance,
    double Faithfulness,
    double ContextPrecision,
    double ContextRecall,
    int InputTokens,
    int OutputTokens,
    double DurationMs);

public interface ISemanticEvaluationJudge
{
    string Provider { get; }
    string Model { get; }

    Task<SemanticEvaluationScore> JudgeAsync(
        SemanticEvaluationRequest request,
        CancellationToken cancellationToken = default);
}
