namespace FocusLedger.Core;

/// <summary>Conservative about missing samples, generous about short input pauses.
/// A sample describes the state AFTER its timestamp. Only observed consecutive
/// intervals are emitted; old idle grace is never retracted.</summary>
public sealed class TrackingEngine
{
    private ActivitySample? previous;
    public const int MaximumGapMs = 5000;
    public void Reset() => previous = null;

    public IReadOnlyList<Segment> Push(ActivitySample current)
    {
        var last = previous;
        previous = current;
        if (last is null) return [];
        var elapsed = current.MonotonicMs - last.MonotonicMs;
        var wallMs = (current.Utc - last.Utc).TotalMilliseconds;
        // Never bill sleep, a stalled collector, clock jumps or backwards time.
        if (elapsed <= 0 || elapsed > MaximumGapMs || wallMs <= 0 || Math.Abs(wallMs - elapsed) > 1500) return [];
        // Non-work states only reset the sampling baseline; never persist runtime coverage.
        if (last.Kind != SegmentKind.Active) return [];

        var remaining = Math.Max(0, last.TimeoutSeconds - last.IdleSeconds);
        // Preserve the exact observed endpoint; floating-point round trips can create tiny idle tails.
        var end = remaining >= wallMs / 1000 ? current.Utc : last.Utc.AddSeconds(remaining);
        List<Segment> result = [];
        if (end > last.Utc) result.Add(new(0, last.Utc, end, last.App, SegmentKind.Active));
        return result;
    }
}
