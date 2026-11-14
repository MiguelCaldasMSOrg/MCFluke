using System.Globalization;
using MCFluke;

namespace MCFluke.Exerciser;

internal interface IClockSyncMeter
{
    uint ReadCounter();
    uint SetTime(TimeOnly target);
}

internal sealed class FlukeClockSyncMeter(FlukeDevice device) : IClockSyncMeter
{
    public uint ReadCounter() => device.ReadClockCounter();
    public uint SetTime(TimeOnly target) => device.SetClock(target);
}

internal sealed record ClockObservation(uint Counter, DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc, TimeSpan Started, TimeSpan Finished)
{
    public TimeSpan Turnaround => Finished - Started;
}

internal readonly record struct ClockOffsetBounds(TimeSpan Lower, TimeSpan Upper)
{
    public bool ContainsZero => Lower <= TimeSpan.Zero && Upper >= TimeSpan.Zero;
    public TimeSpan Midpoint => TimeSpan.FromTicks(Lower.Ticks + (Upper.Ticks - Lower.Ticks) / 2);
}

internal sealed record ClockSyncResult(int Writes, ClockOffsetBounds Offset,
    CommandLatency ReadLatency, CommandLatency SetLatency);

internal static class ClockSynchronization
{
    private const int InitialSamples = 12;
    private const int VerificationSamples = 24;
    private const int MaximumWrites = 4;
    private static readonly TimeSpan Tick = ClockTiming.CounterTick;
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadRequestWire = TimeSpan.FromSeconds(3 * 10d / 9600);
    private static readonly TimeSpan ReadResponseWire = TimeSpan.FromSeconds(34 * 10d / 9600);
    private static readonly TimeSpan SetRequestWire = TimeSpan.FromSeconds(33 * 10d / 9600);

    internal static async Task<ClockSyncResult> RunAsync(IClockSyncMeter meter, IClockSyncTime clock,
        Action<string> log, CancellationToken token)
    {
        try
        {
            return await RunCoreAsync(meter, clock, log, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            log("Synchronization cancelled. No further clock write will be started; any earlier acknowledged write is not undone.");
            throw;
        }
    }

    private static async Task<ClockSyncResult> RunCoreAsync(IClockSyncMeter meter, IClockSyncTime clock,
        Action<string> log, CancellationToken token)
    {
        TimeSpan deadline = clock.Elapsed + MaximumDuration;
        var readSamples = new List<TimeSpan>();
        var setSamples = new List<TimeSpan>();
        log("Reading the running clock and measuring QS turnaround; no writes yet.");
        List<ClockObservation> initial = await ReadSamplesAsync(meter, clock, InitialSamples,
            TimeSpan.FromMilliseconds(150), null, deadline, token);
        ValidateProgress(initial);
        log(FormattableString.Invariant(
            $"Last read: Fluke {ClockTime(initial[^1].Counter):HH:mm:ss.f}; PC after reply {TimeZoneInfo.ConvertTime(initial[^1].FinishedUtc, clock.LocalZone):HH:mm:ss.fff}."));
        readSamples.AddRange(initial.Select(sample => sample.Turnaround));
        CommandLatency latency = CommandLatency.From(readSamples);
        log(Describe("QS", latency));
        // A setter first reads QS, then transmits the longer binary PS request.
        TimeSpan transportOverhead = latency.Median - ReadRequestWire - ReadResponseWire;
        if (transportOverhead < TimeSpan.Zero) transportOverhead = TimeSpan.Zero;
        TimeSpan lead = latency.Median + SetRequestWire + transportOverhead / 2;
        int missedBoundaries = 0;
        ClockOffsetBounds offset = default;
        for (int pass = 1; pass <= MaximumWrites; pass++)
        {
            token.ThrowIfCancellationRequested();
            CheckBudget(clock, deadline);
            ClockCommitPlan plan = ClockTiming.Plan(clock, lead);
            if (plan.SendAt >= deadline)
                throw new ClockSyncException("No feasible clock-setting boundary remains within the 30-second budget.");
            log(FormattableString.Invariant(
                $"Pass {pass}: target local {plan.TargetLocal:HH:mm:ss.f}; setter launch lead {lead.TotalMilliseconds:F1} ms. Keep the optical head steady."));
            await ClockTiming.WaitForSendAsync(clock, plan, token);
            ClockTiming.CheckClock(clock, plan);
            CheckBudget(clock, deadline);
            if (clock.Elapsed - plan.SendAt > TimeSpan.FromMilliseconds(50))
            {
                log("The PC missed this launch deadline by more than 50 ms; no write sent for this boundary.");
                if (++missedBoundaries > 2)
                    throw new ClockSyncException("Too many missed counter boundaries; synchronization aborted.");
                --pass;
                continue;
            }

            token.ThrowIfCancellationRequested();
            TimeSpan started = clock.Elapsed;
            uint written;
            try
            {
                written = meter.SetTime(plan.TargetLocal);
            }
            catch (FlukeException exception)
            {
                throw new ClockSyncException(
                    "Clock setting failed and may already have reached the meter. It will NOT be retried automatically. " +
                    exception.Message, exception);
            }
            TimeSpan finished = clock.Elapsed;
            setSamples.Add(finished - started);
            log(FormattableString.Invariant(
                $"PS acknowledged; complete QS+PS call took {(finished - started).TotalMilliseconds:F1} ms. Reading the clock back."));
            ClockTiming.CheckClock(clock, plan);
            CheckBudget(clock, deadline);
            await clock.DelayAsync(Tick, token);
            List<ClockObservation> samples = await ReadSamplesAsync(meter, clock, VerificationSamples,
                TimeSpan.FromMilliseconds(25), plan, deadline, token);
            readSamples.AddRange(samples.Select(sample => sample.Turnaround));
            offset = EstimateOffset(samples, written, plan.TargetUtc);
            log(FormattableString.Invariant(
                $"Last read: Fluke {ClockTime(samples[^1].Counter):HH:mm:ss.f}; PC after reply {TimeZoneInfo.ConvertTime(samples[^1].FinishedUtc, clock.LocalZone):HH:mm:ss.fff}."));
            log(FormattableString.Invariant(
                $"Observed meter-minus-PC offset envelope: {offset.Lower.TotalMilliseconds:+0.0;-0.0;0.0} .. {offset.Upper.TotalMilliseconds:+0.0;-0.0;0.0} ms."));
            if (offset.ContainsZero)
            {
                var result = new ClockSyncResult(pass, offset,
                    CommandLatency.From(readSamples), CommandLatency.From(setSamples));
                log(Describe("QS", result.ReadLatency));
                log(Describe("QS+PS", result.SetLatency));
                log("Clock readback is consistent with the PC within the reported envelope. This is measured best effort, not a real-time guarantee.");
                return result;
            }
            lead -= offset.Midpoint;
        }
        throw new ClockSyncException(FormattableString.Invariant(
            $"Four clock writes completed, but residual offset remains {offset.Lower.TotalMilliseconds:F1} .. {offset.Upper.TotalMilliseconds:F1} ms. No perfect synchronization is claimed."));
    }

    private static async Task<List<ClockObservation>> ReadSamplesAsync(IClockSyncMeter meter,
        IClockSyncTime clock, int count, TimeSpan spacing, ClockCommitPlan? plan,
        TimeSpan deadline, CancellationToken token)
    {
        var samples = new List<ClockObservation>(count);
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            CheckBudget(clock, deadline);
            if (plan is not null) ClockTiming.CheckClock(clock, plan);
            TimeSpan started = clock.Elapsed;
            DateTimeOffset utc = clock.UtcNow.ToUniversalTime();
            uint counter = meter.ReadCounter();
            TimeSpan finished = clock.Elapsed;
            DateTimeOffset utcEnd = clock.UtcNow.ToUniversalTime();
            CheckBudget(clock, deadline);
            if (((utcEnd - utc) - (finished - started)).Duration() > TimeSpan.FromMilliseconds(50))
                throw new ClockSyncException("The PC clock changed during a clock read.");
            samples.Add(new(counter, utc, utcEnd, started, finished));
            if (index + 1 < count) await clock.DelayAsync(spacing, token);
        }
        return samples;
    }

    private static void CheckBudget(IClockSyncTime clock, TimeSpan deadline)
    {
        if (clock.Elapsed >= deadline)
            throw new ClockSyncException("The 30-second synchronization budget expired; any earlier acknowledged clock write is not undone.");
    }

    internal static void ValidateProgress(IReadOnlyList<ClockObservation> samples)
    {
        ClockObservation first = samples[0], last = samples[^1];
        uint delta = unchecked(last.Counter - first.Counter);
        TimeSpan minimum = last.Started - first.Finished;
        TimeSpan maximum = last.Finished - first.Started;
        TimeSpan device = TimeSpan.FromTicks((long)delta * Tick.Ticks);
        if (device + Tick < minimum || device - Tick > maximum)
            throw new ClockSyncException("QS data does not advance as a running decisecond clock; no clock write is safe.");
    }

    internal static ClockOffsetBounds EstimateOffset(IReadOnlyList<ClockObservation> samples,
        uint writtenCounter, DateTimeOffset targetUtc)
    {
        TimeSpan lower = TimeSpan.MinValue, upper = TimeSpan.MaxValue;
        foreach (ClockObservation sample in samples)
        {
            uint delta = unchecked(sample.Counter - writtenCounter);
            if (delta > 3000)
                throw new ClockSyncException("Clock readback is inconsistent with the counter just written.");
            DateTimeOffset tickStart = targetUtc + TimeSpan.FromTicks((long)delta * Tick.Ticks);
            TimeSpan sampleLower = tickStart - sample.FinishedUtc - TimeSpan.FromMilliseconds(2);
            TimeSpan sampleUpper = tickStart + Tick - sample.StartedUtc + TimeSpan.FromMilliseconds(2);
            if (sampleLower > lower) lower = sampleLower;
            if (sampleUpper < upper) upper = sampleUpper;
        }
        if (lower > upper)
            throw new ClockSyncException("Clock readbacks have inconsistent timing; no accuracy envelope can be established.");
        return new(lower, upper);
    }

    private static string Describe(string name, CommandLatency latency)
        => string.Create(CultureInfo.InvariantCulture,
            $"{name}: {latency.Count} samples; min/median/max {latency.Minimum.TotalMilliseconds:F1}/{latency.Median.TotalMilliseconds:F1}/{latency.Maximum.TotalMilliseconds:F1} ms.");

    private static TimeOnly ClockTime(uint counter)
        => TimeOnly.FromTimeSpan(TimeSpan.FromTicks(
            (long)(counter % FlukeDevice.ClockTicksPerDay) * TimeSpan.TicksPerSecond / FlukeDevice.ClockTicksPerSecond));
}
