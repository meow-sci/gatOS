using System.Diagnostics;

namespace gatOS.Logging;

/// <summary>
///     A fixed-size, allocation-free timing accumulator for one hot path (e.g. the telemetry
///     sample or the MQTT world publish). One writer records each interval with <see cref="Add"/>
///     (or the <see cref="Measure"/> scope); any thread reads the rolled-up figures for display.
/// </summary>
/// <remarks>
///     <para>Recording a sample is two <see cref="Stopwatch.GetTimestamp"/> reads plus a handful of
///     integer stores — no allocation, no lock, no syscall beyond the timestamp (a
///     QueryPerformanceCounter, ~tens of ns). Raw timestamp ticks are accumulated; the
///     ticks→microseconds conversion happens only at read time (the status window), off the hot
///     path.</para>
///     <para>Thread-safety is single-writer / many-reader: each instance is fed by exactly one
///     thread (the sampler's game thread, or a single pump task), so the writer needs no
///     interlock; <see cref="Volatile"/> stores publish the fields to the reader. The reader may
///     observe a one-sample-stale (sum, count) pair — irrelevant for a diagnostic average.</para>
/// </remarks>
public sealed class PerfStat
{
    private static readonly double TicksToMicros = 1_000_000.0 / Stopwatch.Frequency;

    private long _count;
    private long _sumTicks;
    private long _maxTicks;
    private long _lastTicks;

    /// <summary>Starts a scope that records its lifetime into this stat on dispose (alloc-free struct).</summary>
    public Scope Measure() => new(this, Stopwatch.GetTimestamp());

    /// <summary>Records one interval, measured in <see cref="Stopwatch.GetTimestamp"/> ticks.</summary>
    public void Add(long elapsedTicks)
    {
        if (elapsedTicks < 0)
            elapsedTicks = 0; // a clock hiccup must never poison the running sum
        Volatile.Write(ref _sumTicks, _sumTicks + elapsedTicks);
        if (elapsedTicks > _maxTicks)
            Volatile.Write(ref _maxTicks, elapsedTicks);
        Volatile.Write(ref _lastTicks, elapsedTicks);
        // Count is written last (release): a reader that sees the new count also sees the new sum.
        Volatile.Write(ref _count, _count + 1);
    }

    /// <summary>Number of intervals recorded since the last <see cref="Reset"/>.</summary>
    public long Count => Volatile.Read(ref _count);

    /// <summary>Mean interval since the last <see cref="Reset"/>, in microseconds (0 when none).</summary>
    public double AvgMicros
    {
        get
        {
            var count = Volatile.Read(ref _count);
            return count == 0 ? 0 : Volatile.Read(ref _sumTicks) * TicksToMicros / count;
        }
    }

    /// <summary>The most recent interval, in microseconds.</summary>
    public double LastMicros => Volatile.Read(ref _lastTicks) * TicksToMicros;

    /// <summary>The worst interval since the last <see cref="Reset"/>, in microseconds.</summary>
    public double MaxMicros => Volatile.Read(ref _maxTicks) * TicksToMicros;

    /// <summary>Clears all figures (e.g. after the user retunes the cadence and wants fresh numbers).</summary>
    public void Reset()
    {
        Volatile.Write(ref _sumTicks, 0);
        Volatile.Write(ref _maxTicks, 0);
        Volatile.Write(ref _lastTicks, 0);
        Volatile.Write(ref _count, 0);
    }

    /// <summary>A <c>using</c>-scoped timer that records its lifetime into the owning <see cref="PerfStat"/>.</summary>
    public readonly struct Scope(PerfStat stat, long startTimestamp) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => stat.Add(Stopwatch.GetTimestamp() - startTimestamp);
    }
}
