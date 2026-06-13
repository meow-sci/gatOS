using System.Text;
using gatOS.Logging;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.Bus;

/// <summary>
///     The host-side G7 serial bridge: pumps telemetry frames out and serves SCPI command lines
///     in over one duplex byte stream — the QEMU <c>gatos.serial</c> virtio-serial chardev, which
///     the guest sees as <c>/dev/virtio-ports/gatos.serial</c>. Game-free: it speaks only
///     <see cref="SnapshotStore"/> (reads) and <see cref="ICommandSink"/> (writes), exactly like
///     the 9p/HTTP/MQTT transports, so it carries no second copy of the action table.
/// </summary>
/// <remarks>
///     Both telemetry and commands target the <b>active</b> vessel, resolved per frame/line from
///     the latest snapshot (it can change underfoot — switching vessels just redirects the feed).
///     Telemetry is fixed-cadence (a UART is not a firehose); each NDJSON/NMEA frame is
///     line-delimited and each CCSDS frame is length-self-delimited, so a reader can frame the
///     stream without out-of-band signalling. Writes from both directions share one gate.
/// </remarks>
public sealed class SerialBridge
{
    private readonly SnapshotStore _store;
    private readonly SerialMode _mode;
    private readonly TimeSpan _interval;
    private readonly bool _emitTelemetry;
    private readonly ICommandSink? _commands;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _sequence;

    /// <param name="store">The published-snapshot exchange telemetry is read from.</param>
    /// <param name="mode">The telemetry wire format (NDJSON/NMEA/CCSDS).</param>
    /// <param name="interval">Telemetry cadence; ignored when <paramref name="emitTelemetry"/> is false.</param>
    /// <param name="emitTelemetry">When true, stream telemetry frames out at <paramref name="interval"/>.</param>
    /// <param name="commands">
    ///     When non-null, read SCPI command lines in and answer <c>OK</c>/<c>ERR &lt;errno&gt;</c>;
    ///     null disables the command direction (read-only telemetry feed).
    /// </param>
    public SerialBridge(SnapshotStore store, SerialMode mode, TimeSpan interval, bool emitTelemetry,
        ICommandSink? commands)
    {
        _store = store;
        _mode = mode;
        _interval = interval > TimeSpan.Zero ? interval : TimeSpan.FromMilliseconds(500);
        _emitTelemetry = emitTelemetry;
        _commands = commands;
    }

    /// <summary>
    ///     Runs the configured directions on <paramref name="stream"/> until cancelled or the
    ///     stream closes. Returns when both directions have stopped; never throws on a transport
    ///     error (the caller's connector reconnects).
    /// </summary>
    public async Task RunAsync(Stream stream, CancellationToken ct)
    {
        var directions = new List<Task>(2);
        if (_emitTelemetry)
            directions.Add(PumpTelemetryAsync(stream, ct));
        if (_commands is not null)
            directions.Add(ServeCommandsAsync(stream, _commands, ct));
        if (directions.Count == 0)
            return;

        try
        {
            await Task.WhenAll(directions).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Transport gone / cancelled — the connector decides whether to reconnect.
        }
    }

    private async Task PumpTelemetryAsync(Stream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var snapshot = _store.Current;
            if (ActiveVessel(snapshot) is { } vessel)
            {
                var frame = SerialTelemetry.Frame(_mode, snapshot, vessel, Interlocked.Increment(ref _sequence));
                if (frame.Length > 0)
                    await WriteAsync(stream, frame, ct).ConfigureAwait(false);
            }

            await Task.Delay(_interval, ct).ConfigureAwait(false);
        }
    }

    private async Task ServeCommandsAsync(Stream stream, ICommandSink sink, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var line = new List<byte>(128);
        while (!ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                return; // guest closed the port

            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b is (byte)'\n' or (byte)'\r')
                {
                    if (line.Count > 0)
                    {
                        await HandleLineAsync(stream, sink, Encoding.UTF8.GetString(line.ToArray()), ct)
                            .ConfigureAwait(false);
                        line.Clear();
                    }
                }
                else if (line.Count < 1024) // bound a runaway unterminated line
                {
                    line.Add(b);
                }
            }
        }
    }

    private async Task HandleLineAsync(Stream stream, ICommandSink sink, string text, CancellationToken ct)
    {
        // The active vessel is the command target, resolved per line (it can change underfoot).
        var vesselId = _store.Current.ActiveVesselId;
        string response;
        if (vesselId is null)
            response = "ERR ENOENT";
        else
            response = await new ScpiCommandPort(sink, vesselId).HandleAsync(text, ct).ConfigureAwait(false);
        // LF-terminated (not CRLF): a guest `read` would otherwise keep a trailing CR on the reply.
        await WriteAsync(stream, Encoding.ASCII.GetBytes(response + "\n"), ct).ConfigureAwait(false);
    }

    private async Task WriteAsync(Stream stream, byte[] bytes, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static VesselSnapshot? ActiveVessel(SimSnapshot snapshot)
        => snapshot.Vessels.FirstOrDefault(v => v.Id == snapshot.ActiveVesselId)
           ?? (snapshot.Vessels.Count > 0 ? snapshot.Vessels[0] : null);
}
