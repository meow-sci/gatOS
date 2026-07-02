using System.Diagnostics;

namespace gatOS.NineP.Server;

/// <summary>
///     Allocation-free transport counters for one <see cref="NinePServer"/>
///     (PERF_IMPROVEMENT_PLAN.md P4/P8): how many <c>Tread</c>s the server answered, how many
///     payload bytes they carried, and how long reply writes take. Written by session tasks
///     (interlocked — sessions run concurrently), read by the in-game status window.
/// </summary>
public sealed class NinePServerStats
{
    private static readonly double TicksToMicros = 1_000_000.0 / Stopwatch.Frequency;

    private long _treadCount;
    private long _treadBytes;
    private long _sendCount;
    private long _sendTicks;

    /// <summary>Total <c>Rread</c> replies served since start/reset.</summary>
    public long TreadCount => Interlocked.Read(ref _treadCount);

    /// <summary>Total payload bytes those replies carried.</summary>
    public long TreadBytes => Interlocked.Read(ref _treadBytes);

    /// <summary>Mean reply socket-write time in microseconds (all message types).</summary>
    public double SendAvgMicros
    {
        get
        {
            var count = Interlocked.Read(ref _sendCount);
            return count == 0 ? 0 : Interlocked.Read(ref _sendTicks) * TicksToMicros / count;
        }
    }

    /// <summary>Clears all figures (the status window's "Reset perf" button).</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _treadCount, 0);
        Interlocked.Exchange(ref _treadBytes, 0);
        Interlocked.Exchange(ref _sendCount, 0);
        Interlocked.Exchange(ref _sendTicks, 0);
    }

    internal void RecordTread(int payloadBytes)
    {
        Interlocked.Increment(ref _treadCount);
        Interlocked.Add(ref _treadBytes, payloadBytes);
    }

    internal void RecordSend(long elapsedTicks)
    {
        Interlocked.Increment(ref _sendCount);
        Interlocked.Add(ref _sendTicks, elapsedTicks);
    }
}
