namespace PhoneBox.Agent;

/// <summary>Computes clamped media playback position and duration from SMTC timeline properties.</summary>
internal static class MediaTimeline
{
    public const long MaxSeekMs = 86_400_000L * 365;

    public static bool TryComputeSeekTicks(TimeSpan startTime, long ms, out long targetTicks)
    {
        targetTicks = 0;
        if (ms is < 0 or > MaxSeekMs) return false;
        var deltaTicks = ms * TimeSpan.TicksPerMillisecond;
        if (startTime.Ticks > long.MaxValue - deltaTicks) return false;
        targetTicks = startTime.Ticks + deltaTicks;
        return true;
    }

    public static (long PosMs, long DurMs) Compute(
        TimeSpan position, TimeSpan startTime, TimeSpan endTime,
        DateTimeOffset lastUpdatedTime, bool playing, DateTimeOffset now)
    {
        var dur = Math.Max(0, (long)(endTime - startTime).TotalMilliseconds);
        var pos = (long)(position - startTime).TotalMilliseconds;
        if (playing && lastUpdatedTime.Year > 2000)
            pos += (long)Math.Max(0, (now - lastUpdatedTime).TotalMilliseconds);
        if (dur > 0) pos = Math.Clamp(pos, 0, dur);
        else if (pos < 0) pos = 0;
        return (pos, dur);
    }
}
