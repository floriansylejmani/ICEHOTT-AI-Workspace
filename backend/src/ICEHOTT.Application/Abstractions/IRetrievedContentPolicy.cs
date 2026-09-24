namespace ICEHOTT.Application.Abstractions;

public sealed record RetrievedContentDecision(bool Allowed, string? Reason);

public interface IRetrievedContentPolicy
{
    RetrievedContentDecision Evaluate(string content);
}
