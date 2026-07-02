namespace gatOS.Logging;

/// <summary>
///     A fixed-size, allocation-free accumulator for one hot-path <b>value</b> series (e.g. bytes
///     allocated per telemetry sample) — the raw-number sibling of <see cref="PerfStat"/>, which is
///     specialized for timestamp intervals. One writer records each observation with
///     <see cref="Add"/>; any thread reads the rolled-up figures for display.
/// </summary>
/// <remarks>
///     Thread-safety is single-writer / many-reader, exactly like <see cref="PerfStat"/>: each
///     instance is fed by one thread (the sampler's game thread, or a single pump task), so the
///     writer needs no interlock; <see cref="Volatile"/> stores publish the fields to the reader.
///     The reader may observe a one-sample-stale (sum, count) pair — irrelevant for diagnostics.
/// </remarks>
public sealed class ValueStat
{
    private long _count;
    private long _sum;
    private long _max;
    private long _last;

    /// <summary>Records one observation. Negative values are clamped to 0 (a torn read must never poison the sum).</summary>
    public void Add(long value)
    {
        if (value < 0)
            value = 0;
        Volatile.Write(ref _sum, _sum + value);
        if (value > _max)
            Volatile.Write(ref _max, value);
        Volatile.Write(ref _last, value);
        // Count is written last (release): a reader that sees the new count also sees the new sum.
        Volatile.Write(ref _count, _count + 1);
    }

    /// <summary>Number of observations recorded since the last <see cref="Reset"/>.</summary>
    public long Count => Volatile.Read(ref _count);

    /// <summary>Mean observed value since the last <see cref="Reset"/> (0 when none).</summary>
    public double Avg
    {
        get
        {
            var count = Volatile.Read(ref _count);
            return count == 0 ? 0 : (double)Volatile.Read(ref _sum) / count;
        }
    }

    /// <summary>The most recent observation.</summary>
    public long Last => Volatile.Read(ref _last);

    /// <summary>The largest observation since the last <see cref="Reset"/>.</summary>
    public long Max => Volatile.Read(ref _max);

    /// <summary>Clears all figures.</summary>
    public void Reset()
    {
        Volatile.Write(ref _sum, 0);
        Volatile.Write(ref _max, 0);
        Volatile.Write(ref _last, 0);
        Volatile.Write(ref _count, 0);
    }
}
