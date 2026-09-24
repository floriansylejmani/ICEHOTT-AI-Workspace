using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ICEHOTT.Application.Knowledge;

public static class RagTelemetry
{
    public const string SourceName = "ICEHOTT.Rag";
    public static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");
    public static readonly Meter Meter = new(SourceName, "1.0.0");

    public static readonly Counter<long> RetrievalRequests =
        Meter.CreateCounter<long>("rag.retrieval.requests");
    public static readonly Counter<long> FilteredChunks =
        Meter.CreateCounter<long>("rag.retrieval.filtered_chunks");
    public static readonly Histogram<double> RetrievalDurationMs =
        Meter.CreateHistogram<double>("rag.retrieval.duration.ms");
    public static readonly Histogram<long> RetrievalResults =
        Meter.CreateHistogram<long>("rag.retrieval.results");

    public static readonly Histogram<double> IndexingDurationMs =
        Meter.CreateHistogram<double>("rag.indexing.duration.ms");
    public static readonly Histogram<long> IndexedChunks =
        Meter.CreateHistogram<long>("rag.indexing.chunks");
    public static readonly Counter<long> IndexingFailures =
        Meter.CreateCounter<long>("rag.indexing.failures");

    public static readonly Counter<long> JobsCompleted =
        Meter.CreateCounter<long>("rag.jobs.completed");
    public static readonly Counter<long> JobsRetried =
        Meter.CreateCounter<long>("rag.jobs.retried");
    public static readonly Counter<long> JobsFailed =
        Meter.CreateCounter<long>("rag.jobs.failed");
}
