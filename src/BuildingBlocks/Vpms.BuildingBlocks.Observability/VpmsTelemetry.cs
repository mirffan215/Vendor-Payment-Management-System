using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Vpms.BuildingBlocks.Observability;

/// <summary>The application's own signals. Alerts in the deployment are written against these names.</summary>
public static class VpmsTelemetry
{
    public const string Name = "Vpms";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    private static readonly Counter<long> SecurityEventCounter = Meter.CreateCounter<long>("vpms.security.events", description: "Security-relevant events by type (login failure, step-up required, access denied, csrf failure, rate limited).");
    private static readonly Counter<long> JobRunCounter = Meter.CreateCounter<long>("vpms.job.runs", description: "Background job passes by job and outcome.");
    private static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>("vpms.job.duration", unit: "ms", description: "How long a background job pass took.");

    public static void SecurityEvent(string type) => SecurityEventCounter.Add(1, new KeyValuePair<string, object?>("type", type));

    public static void JobRun(string job, bool succeeded, TimeSpan elapsed)
    {
        JobRunCounter.Add(1, new KeyValuePair<string, object?>("job", job), new KeyValuePair<string, object?>("outcome", succeeded ? "ok" : "failed"));
        JobDuration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("job", job));
    }

    /// <summary>Registers a gauge read on demand, such as the number of dead-lettered messages.</summary>
    public static ObservableGauge<long> Gauge(string name, Func<long> read, string? description = null) => Meter.CreateObservableGauge(name, read, description: description);
}
