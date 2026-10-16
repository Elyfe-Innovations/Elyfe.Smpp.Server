using System.Diagnostics.Metrics;

namespace Elyfe.Smpp.Server;

/// <summary>
///     OpenTelemetry-compatible metrics for the SMPP server, published through a
///     <see cref="System.Diagnostics.Metrics.Meter" /> named <see cref="MeterName" />. Register the meter with your
///     OpenTelemetry pipeline (e.g. <c>builder.AddMeter(SmppServerMetrics.MeterName)</c>) to export the instruments.
/// </summary>
public sealed class SmppServerMetrics : IDisposable
{
    /// <summary>The meter name to register with an OpenTelemetry <c>MeterProvider</c>.</summary>
    public const string MeterName = "Elyfe.Smpp.Server";

    private readonly Meter _meter;
    private readonly Counter<long> _binds;
    private readonly Counter<long> _submits;
    private readonly Counter<long> _errors;
    private readonly Histogram<double> _submitDuration;
    private Func<int>? _activeSessionsObserver;

    /// <summary>Creates the metric instruments.</summary>
    public SmppServerMetrics()
    {
        _meter = new Meter(MeterName);
        _binds = _meter.CreateCounter<long>(
            "smpp.server.binds", unit: "{bind}", description: "Number of SMPP bind attempts by result.");
        _submits = _meter.CreateCounter<long>(
            "smpp.server.submits", unit: "{message}", description: "Number of inbound submit_sm/data_sm by result.");
        _errors = _meter.CreateCounter<long>(
            "smpp.server.errors", unit: "{error}", description: "Number of session/PDU processing errors.");
        _submitDuration = _meter.CreateHistogram<double>(
            "smpp.server.submit.duration", unit: "ms", description: "Latency of inbound submit handling.");
        _meter.CreateObservableGauge(
            "smpp.server.sessions.active",
            () => _activeSessionsObserver?.Invoke() ?? 0,
            unit: "{session}",
            description: "Currently active SMPP sessions.");
    }

    /// <summary>Registers a callback supplying the current active-session count for the observable gauge.</summary>
    public void TrackActiveSessions(Func<int> observer) => _activeSessionsObserver = observer;

    /// <summary>Records a bind attempt with its outcome (<c>ok</c>, <c>auth_failed</c>, <c>rejected</c>, ...).</summary>
    public void RecordBind(string result)
        => _binds.Add(1, new KeyValuePair<string, object?>("result", result));

    /// <summary>Records an inbound submit with its outcome.</summary>
    public void RecordSubmit(string result)
        => _submits.Add(1, new KeyValuePair<string, object?>("result", result));

    /// <summary>Records the latency of submit handling in milliseconds.</summary>
    public void RecordSubmitDuration(double milliseconds)
        => _submitDuration.Record(milliseconds);

    /// <summary>Records a processing error categorised by <paramref name="kind" />.</summary>
    public void RecordError(string kind)
        => _errors.Add(1, new KeyValuePair<string, object?>("kind", kind));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
