namespace MCFluke.Exerciser;

internal interface IClockSyncTime
{
    DateTimeOffset UtcNow { get; }
    TimeSpan Elapsed { get; }
    TimeZoneInfo LocalZone { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken token);
    Task WaitPreciselyAsync(TimeSpan delay, CancellationToken token) => DelayAsync(delay, token);
}

internal sealed class SystemClockSyncTime : IClockSyncTime
{
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeSpan Elapsed => clock.Elapsed;
    public TimeZoneInfo LocalZone => TimeZoneInfo.Local;
    public Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);

    public Task WaitPreciselyAsync(TimeSpan delay, CancellationToken token)
    {
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromMilliseconds(30))
            throw new ArgumentOutOfRangeException(nameof(delay), "The bounded final wait must be at most 30 ms.");
        TimeSpan until = clock.Elapsed + delay;
        while (clock.Elapsed < until)
        {
            token.ThrowIfCancellationRequested();
            Thread.SpinWait(32);
        }
        return Task.CompletedTask;
    }
}

internal sealed record CommandLatency(TimeSpan Minimum, TimeSpan Median, TimeSpan Maximum, int Count)
{
    internal static CommandLatency From(IEnumerable<TimeSpan> samples)
    {
        TimeSpan[] sorted = samples.Order().ToArray();
        if (sorted.Length == 0 || sorted.Any(sample => sample <= TimeSpan.Zero))
            throw new ArgumentException("Positive command turnaround samples are required.", nameof(samples));
        TimeSpan median = sorted.Length % 2 == 0
            ? TimeSpan.FromTicks(sorted[sorted.Length / 2 - 1].Ticks +
                (sorted[sorted.Length / 2].Ticks - sorted[sorted.Length / 2 - 1].Ticks) / 2)
            : sorted[sorted.Length / 2];
        return new(sorted[0], median, sorted[^1], sorted.Length);
    }
}

internal sealed record ClockCommitPlan(
    DateTimeOffset TargetUtc,
    TimeOnly TargetLocal,
    TimeSpan SendAt,
    TimeSpan SendLead,
    DateTimeOffset ReferenceUtc,
    TimeSpan ReferenceElapsed,
    TimeSpan LocalOffset);

internal sealed class ClockSyncException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

internal static class ClockTiming
{
    internal static ClockCommitPlan Plan(IClockSyncTime clock, TimeSpan sendLead)
    {
        if (sendLead < TimeSpan.FromSeconds(-10) || sendLead > TimeSpan.FromSeconds(10))
            throw new ClockSyncException("Measured clock-setting latency is too large for safe minute-boundary scheduling.");
        DateTimeOffset reference = clock.UtcNow.ToUniversalTime();
        TimeSpan referenceElapsed = clock.Elapsed;
        DateTimeOffset earliest = reference + (sendLead > TimeSpan.Zero ? sendLead : TimeSpan.Zero) + TimeSpan.FromSeconds(5);
        DateTimeOffset target = earliest.AddTicks(-(earliest.Ticks % TimeSpan.TicksPerMinute)).AddMinutes(1);
        DateTimeOffset local = TimeZoneInfo.ConvertTime(target, clock.LocalZone);
        return new(target, TimeOnly.FromDateTime(local.DateTime),
            referenceElapsed + (target - reference) - sendLead,
            sendLead, reference, referenceElapsed, local.Offset);
    }

    internal static void CheckClock(IClockSyncTime clock, ClockCommitPlan plan)
    {
        DateTimeOffset expected = plan.ReferenceUtc + (clock.Elapsed - plan.ReferenceElapsed);
        if ((clock.UtcNow - expected).Duration() > TimeSpan.FromMilliseconds(250))
            throw new ClockSyncException("The PC clock changed; synchronization stopped. Any earlier acknowledged clock write is not undone.");
        if (TimeZoneInfo.ConvertTime(plan.TargetUtc, clock.LocalZone).Offset != plan.LocalOffset)
            throw new ClockSyncException("The PC time zone changed; synchronization stopped. Any earlier acknowledged clock write is not undone.");
    }

    internal static async Task WaitForSendAsync(IClockSyncTime clock, ClockCommitPlan plan, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            CheckClock(clock, plan);
            TimeSpan remaining = plan.SendAt - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) return;
            if (remaining <= TimeSpan.FromMilliseconds(30))
                await clock.WaitPreciselyAsync(remaining, token);
            else
                await clock.DelayAsync(remaining > TimeSpan.FromMilliseconds(200)
                    ? TimeSpan.FromMilliseconds(100) : remaining - TimeSpan.FromMilliseconds(20), token);
        }
    }
}
