using System.Diagnostics.Metrics;

namespace SuperBackend.Platform;

public static class OperationsTelemetry
{
    public const string MeterName = "SuperBackend.Operations";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("superbackend.operation.outcomes");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("superbackend.operation.duration", "s");

    public static void Record(string component, string outcome, double seconds, int count = 1)
    {
        var componentTag = new KeyValuePair<string, object?>("component", component);
        var outcomeTag = new KeyValuePair<string, object?>("outcome", outcome);
        Outcomes.Add(count, componentTag, outcomeTag);
        Duration.Record(seconds, componentTag, outcomeTag);
    }
}
