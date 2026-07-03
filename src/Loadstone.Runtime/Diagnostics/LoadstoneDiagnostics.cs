using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Loadstone.Runtime.Diagnostics;

/// <summary>
/// Single home for Loadstone's traces and metrics. Hook these names into any OpenTelemetry
/// pipeline (<c>AddSource("Loadstone")</c> / <c>AddMeter("Loadstone")</c>) and every import
/// emits spans and counters without further wiring.
/// </summary>
public static class LoadstoneDiagnostics
{
    public const string ActivitySourceName = "Loadstone";

    public const string MeterName = "Loadstone";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> JobsCompleted = Meter.CreateCounter<long>(
        "loadstone.jobs.completed", description: "Import jobs finished, tagged by dataset and outcome.");

    public static readonly Counter<long> RecordsRead = Meter.CreateCounter<long>(
        "loadstone.records.read", description: "Source records parsed, tagged by dataset.");

    public static readonly Counter<long> RecordsRejected = Meter.CreateCounter<long>(
        "loadstone.records.rejected", description: "Records diverted to the rejection store, tagged by dataset.");

    public static readonly Counter<long> RowsWritten = Meter.CreateCounter<long>(
        "loadstone.rows.written", description: "Rows inserted or updated in target tables, tagged by dataset and operation.");

    public static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>(
        "loadstone.job.duration", unit: "s", description: "End-to-end import job duration, tagged by dataset and outcome.");
}
