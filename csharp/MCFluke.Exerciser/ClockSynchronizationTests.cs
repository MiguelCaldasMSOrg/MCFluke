using MCFluke;
using static MCFluke.Exerciser.SelfTests;

namespace MCFluke.Exerciser;

internal static class ClockSynchronizationTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases()
    {
        yield return ("clock latency statistics and bounded scheduling", Statistics);
        yield return ("clock synchronization compensates RMW, wire delay and divider phase", Synchronizes);
        yield return ("clock synchronization stops after one satisfactory write", StopsAfterFirstGoodWrite);
        yield return ("clock synchronization allows a deferred firmware latch after ACK", DeferredLatch);
        yield return ("clock synchronization handles midnight and non-UTC local time", Midnight);
        yield return ("clock synchronization rejects a frozen counter before any write", FrozenCounter);
        yield return ("clock synchronization aborts on a PC clock step before writing", ClockStep);
        yield return ("clock synchronization aborts on a PC time-zone change before writing", ZoneChange);
        yield return ("clock synchronization skips a late decisecond boundary without writing", LateBoundary);
        yield return ("clock synchronization never retries an ambiguous write", AmbiguousWrite);
        yield return ("clock synchronization rejects inconsistent post-write readback", InvalidReadback);
        yield return ("clock synchronization stops after four nonconvergent writes", Nonconvergent);
        yield return ("clock synchronization honors cancellation before I/O", Cancelled);
        yield return ("clock synchronization cancels during a boundary wait without writing", CancelledWhileWaiting);
        yield return ("clock synchronization enforces its short total time budget", TimeBudget);
        yield return ("clock synchronization cancellation does not pretend to undo an acknowledged write", CancelledAfterWrite);
        yield return ("clock readback offset arithmetic handles uint32 counter rollover", CounterRollover);
    }

    private static readonly DateTimeOffset Origin = new(2026, 11, 14, 17, 48, 10, TimeSpan.Zero);

    private static void Statistics()
    {
        CommandLatency latency = CommandLatency.From(new[]
        {
            TimeSpan.FromMilliseconds(90), TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(50)
        });
        Equal(TimeSpan.FromMilliseconds(40), latency.Median);
        Equal(TimeSpan.FromMilliseconds(10), latency.Minimum);
        Equal(TimeSpan.FromMilliseconds(90), latency.Maximum);
        Throws<ArgumentException>(() => CommandLatency.From([]));
        Throws<ArgumentException>(() => CommandLatency.From([TimeSpan.Zero]));
        var clock = new FakeTime(Origin);
        ClockCommitPlan plan = ClockTiming.Plan(clock, TimeSpan.FromMilliseconds(-40));
        Equal(0L, plan.TargetUtc.Ticks % TimeSpan.FromMilliseconds(100).Ticks);
        Require(plan.TargetUtc - Origin <= TimeSpan.FromMilliseconds(150),
            "A feasible decisecond target must not wait for a whole second or minute.");
        Require(plan.SendAt > plan.TargetUtc - Origin, "A negative lead should launch after the target boundary.");
        Throws<ClockSyncException>(() => ClockTiming.Plan(clock, TimeSpan.FromSeconds(11)));
    }

    private static void Synchronizes()
    {
        (int Outbound, int Phase)[] scenarios = [(160, 0), (160, 40), (160, 90), (35, 90)];
        foreach (var scenario in scenarios)
        {
            var clock = new FakeTime(Origin);
            var meter = new FakeMeter(clock)
            {
                Outbound = TimeSpan.FromMilliseconds(scenario.Outbound),
                DividerPhase = TimeSpan.FromMilliseconds(scenario.Phase)
            };
            var messages = new List<string>();
            ClockSyncResult result = Run(meter, clock, messages.Add);
            Equal(2, result.Writes);
            Equal(2, meter.SetCalls);
            Require(result.Offset.ContainsZero, "Readbacks should bracket zero offset.");
            Require(meter.Targets.All(time => time.Ticks % TimeSpan.FromMilliseconds(100).Ticks == 0),
                "All writes must target exact deciseconds.");
            Require(meter.Targets.Any(time => time.Millisecond != 0),
                "Targets should use the available decisecond resolution.");
            Require(clock.Elapsed < TimeSpan.FromSeconds(10), "Normal synchronization should finish within seconds.");
            Require(messages.Any(message => message.Contains("setter launch lead", StringComparison.Ordinal)),
                "Missing latency-compensation diagnostics.");
            Require(meter.SetterLaunches.Distinct().Count() == 2, "Expected only the required correction.");
            if (scenario.Outbound == 35)
            {
                var target = new DateTimeOffset(Origin.Date + meter.Targets[^1].ToTimeSpan(), TimeSpan.Zero);
                Require(meter.SetterLaunches[^1] > target - Origin,
                    "Divider-phase compensation should be able to launch after the target boundary.");
            }
        }
    }

    private static void Midnight()
    {
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("test +05:30", TimeSpan.FromMinutes(330), "test", "test");
        var clock = new FakeTime(new DateTimeOffset(2026, 11, 14, 18, 29, 57, 800, TimeSpan.Zero), zone);
        var meter = new FakeMeter(clock);
        ClockSyncResult result = Run(meter, clock, _ => { });
        Equal(1, result.Writes);
        Equal(new TimeOnly(0, 0, 0, 100), meter.Targets[0]);
    }

    private static void DeferredLatch()
    {
        var clock = new FakeTime(Origin);
        var meter = new FakeMeter(clock) { DeferredApply = TimeSpan.FromMilliseconds(50) };
        ClockSyncResult result = Run(meter, clock, _ => { });
        Equal(2, result.Writes);
        Require(result.Offset.ContainsZero, "Deferred clock application was not compensated.");
    }

    private static void FrozenCounter()
    {
        var clock = new FakeTime(Origin);
        var meter = new FakeMeter(clock) { Frozen = true };
        Throws<ClockSyncException>(() => Run(meter, clock, _ => { }));
        Equal(0, meter.SetCalls);
    }

    private static void ClockStep()
    {
        var clock = new FakeTime(Origin) { StepAt = TimeSpan.FromMilliseconds(2200) };
        var meter = new FakeMeter(clock);
        Throws<ClockSyncException>(() => Run(meter, clock, _ => { }));
        Equal(0, meter.SetCalls);
    }

    private static void LateBoundary()
    {
        var clock = new FakeTime(Origin) { OversleepOnce = TimeSpan.FromMilliseconds(250) };
        var meter = new FakeMeter(clock);
        var messages = new List<string>();
        ClockSyncResult result = Run(meter, clock, messages.Add);
        Equal(1, result.Writes);
        Require(meter.Targets[0] > new TimeOnly(17, 48, 12, 400), "The missed target must not be written late.");
        Require(clock.Elapsed < TimeSpan.FromSeconds(6), "A missed boundary must not add a minute of waiting.");
        Require(messages.Any(message => message.Contains("no write sent", StringComparison.Ordinal)),
            "A missed deadline should be reported, not silently used.");
    }

    private static void ZoneChange()
    {
        var clock = new FakeTime(Origin) { ZoneChangeAt = TimeSpan.FromMilliseconds(2200) };
        var meter = new FakeMeter(clock);
        Throws<ClockSyncException>(() => Run(meter, clock, _ => { }));
        Equal(0, meter.SetCalls);
    }

    private static void AmbiguousWrite()
    {
        var clock = new FakeTime(Origin);
        var meter = new FakeMeter(clock) { FailSet = true };
        ClockSyncException exception = Throws<ClockSyncException>(() => Run(meter, clock, _ => { }));
        Require(exception.Message.Contains("NOT be retried", StringComparison.Ordinal), "Missing uncertain-delivery warning.");
        Equal(1, meter.SetCalls);
    }

    private static void InvalidReadback()
    {
        var clock = new FakeTime(Origin);
        var meter = new FakeMeter(clock) { BadReadback = true };
        Throws<ClockSyncException>(() => Run(meter, clock, _ => { }));
        Equal(1, meter.SetCalls);
    }

    private static void Nonconvergent()
    {
        var clock = new FakeTime(Origin);
        var meter = new FakeMeter(clock) { VaryOutbound = true };
        Throws<ClockSyncException>(() => Run(meter, clock, _ => { }));
        Equal(4, meter.SetCalls);
    }

    private static void Cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var clock = new FakeTime(Origin);
        var meter = new FakeMeter(clock);
        Throws<OperationCanceledException>(() => ClockSynchronization.RunAsync(meter, clock, _ => { },
            cancellation.Token).GetAwaiter().GetResult());
        Equal(0, meter.ReadCalls);
        Equal(0, meter.SetCalls);
    }

    private static void CounterRollover()
    {
        var sample = new ClockObservation(0, Origin + TimeSpan.FromMilliseconds(200),
            Origin + TimeSpan.FromMilliseconds(300), TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        Require(ClockSynchronization.EstimateOffset([sample], uint.MaxValue - 1, Origin).ContainsZero,
            "An unsigned counter rollover was mistaken for a huge clock error.");
    }

    private static void CancelledWhileWaiting()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new FakeTime(Origin) { CancelAt = TimeSpan.FromMilliseconds(2200), Cancellation = cancellation };
        var meter = new FakeMeter(clock);
        Throws<OperationCanceledException>(() => ClockSynchronization.RunAsync(meter, clock, _ => { },
            cancellation.Token).GetAwaiter().GetResult());
        Equal(0, meter.SetCalls);
    }

    private static void CancelledAfterWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new FakeTime(Origin) { CancelAt = TimeSpan.FromMilliseconds(2600), Cancellation = cancellation };
        var meter = new FakeMeter(clock);
        var messages = new List<string>();
        Throws<OperationCanceledException>(() => ClockSynchronization.RunAsync(meter, clock, messages.Add,
            cancellation.Token).GetAwaiter().GetResult());
        Equal(1, meter.SetCalls);
        Require(messages.Any(message => message.Contains("not undone", StringComparison.Ordinal)),
            "Cancellation must not claim to roll back an already applied clock change.");
    }

    private static ClockSyncResult Run(FakeMeter meter, FakeTime clock, Action<string> log)
        => ClockSynchronization.RunAsync(meter, clock, log, CancellationToken.None).GetAwaiter().GetResult();

    private static void StopsAfterFirstGoodWrite()
    {
        var clock = new FakeTime(Origin);
        var meter = new FakeMeter(clock);
        ClockSyncResult result = Run(meter, clock, _ => { });
        Equal(1, result.Writes);
        Equal(1, meter.SetCalls);
        Require(clock.Elapsed < TimeSpan.FromSeconds(5), "A satisfactory first write should stop immediately after verification.");
    }

    private static void TimeBudget()
    {
        var clock = new FakeTime(Origin) { DelayOverhead = TimeSpan.FromSeconds(5) };
        var meter = new FakeMeter(clock);
        Throws<ClockSyncException>(() => Run(meter, clock, _ => { }));
        Equal(0, meter.SetCalls);
        Require(clock.Elapsed < TimeSpan.FromSeconds(36), "The time budget must be checked during initial sampling.");
    }

    private sealed class FakeTime(DateTimeOffset origin, TimeZoneInfo? zone = null) : IClockSyncTime
    {
        public TimeSpan Elapsed { get; private set; }
        private static readonly TimeZoneInfo ShiftedZone =
            TimeZoneInfo.CreateCustomTimeZone("changed +01", TimeSpan.FromHours(1), "changed", "changed");
        public TimeZoneInfo LocalZone => ZoneChangeAt is TimeSpan change && Elapsed >= change
            ? ShiftedZone : zone ?? TimeZoneInfo.Utc;
        public TimeSpan? StepAt { get; init; }
        public TimeSpan? ZoneChangeAt { get; init; }
        public TimeSpan OversleepOnce { get; set; }
        public TimeSpan DelayOverhead { get; init; }
        public TimeSpan? CancelAt { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }
        public DateTimeOffset UtcNow => origin + Elapsed +
            (StepAt is TimeSpan step && Elapsed >= step ? TimeSpan.FromSeconds(2) : TimeSpan.Zero);

        public void Advance(TimeSpan amount)
        {
            Elapsed += amount;
            if (CancelAt is TimeSpan cancelAt && Elapsed >= cancelAt) Cancellation?.Cancel();
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Advance(delay + DelayOverhead);
            if (delay <= TimeSpan.FromMilliseconds(30) && OversleepOnce > TimeSpan.Zero)
            {
                Advance(OversleepOnce);
                OversleepOnce = TimeSpan.Zero;
            }
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMeter(FakeTime clock) : IClockSyncMeter
    {
        private uint counter = 12345;
        private TimeSpan counterStarted;
        private uint? pendingCounter;
        private TimeSpan pendingAt;
        public int SetCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public List<TimeOnly> Targets { get; } = [];
        public List<TimeSpan> SetterLaunches { get; } = [];
        public TimeSpan Outbound { get; init; } = TimeSpan.FromMilliseconds(80);
        public TimeSpan DividerPhase { get; init; } = TimeSpan.FromMilliseconds(40);
        public TimeSpan DeferredApply { get; init; }
        public bool Frozen { get; init; }
        public bool FailSet { get; init; }
        public bool BadReadback { get; init; }
        public bool VaryOutbound { get; init; }

        public uint ReadCounter()
        {
            ReadCalls++;
            clock.Advance(TimeSpan.FromMilliseconds(10));
            if (pendingCounter is uint pending && clock.Elapsed >= pendingAt)
            {
                counter = pending;
                counterStarted = pendingAt - DividerPhase;
                pendingCounter = null;
            }
            uint result = Frozen ? 12345 : unchecked(counter +
                (uint)((clock.Elapsed - counterStarted).Ticks / TimeSpan.FromMilliseconds(100).Ticks));
            if (BadReadback && SetCalls > 0) result = unchecked(result + 100000);
            clock.Advance(TimeSpan.FromMilliseconds(30));
            return result;
        }

        public uint SetTime(TimeOnly target)
        {
            SetCalls++;
            Targets.Add(target);
            SetterLaunches.Add(clock.Elapsed);
            if (FailSet) throw new FlukeProtocolException(0, "test write", "Injected ambiguous write failure.");
            clock.Advance(TimeSpan.FromMilliseconds(40));
            clock.Advance(VaryOutbound && SetCalls % 2 == 1 ? TimeSpan.FromMilliseconds(600) : Outbound);
            uint written = (uint)(target.Ticks / TimeSpan.FromMilliseconds(100).Ticks);
            if (DeferredApply > TimeSpan.Zero)
            {
                pendingCounter = written;
                pendingAt = clock.Elapsed + DeferredApply;
            }
            else
            {
                counter = written;
                counterStarted = clock.Elapsed - DividerPhase;
            }
            clock.Advance(TimeSpan.FromMilliseconds(20));
            return written;
        }
    }
}
