using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MCFluke;

namespace MCFluke.Exerciser;

internal static class SelfTests
{
    internal static async Task<int> RunAsync(CancellationToken token)
    {
        int passed = 0, failed = 0;
        void Run(string name, Action test)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                test();
                passed++;
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {exception}");
            }
        }

        Console.WriteLine("Offline self-tests: ONLY sim://187 is ever opened. No real serial port is used.");
        Run("ABI 1 native sizes, offsets and int32 enums", TestLayouts);
        Run("CLI parsing and simulator isolation", TestOptions);
        Run("short PC-clock timestamps and concise invariant numeric output", TestReadingFormat);
        Run("managed argument guards (no native calls)", TestManagedGuards);
        Run("typed exception mappings preserve structured details", TestExceptionMappings);
        Run("mismatched and unknown structured statuses are protocol errors", TestMalformedErrors);
        foreach (var test in ClockSynchronizationTests.Cases())
            Run(test.Name, test.Test);

        try
        {
            token.ThrowIfCancellationRequested();
            FlukeProtocol.EnsureCompatibleAbi();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"BLOCKED native simulator tests: {exception.Message}");
            Console.Error.WriteLine("Build the native CMake library or set absolute MCFLUKE_LIBRARY; no hardware fallback was attempted.");
            Console.WriteLine($"Managed results: {passed} passed, {failed} failed; native coverage NOT RUN.");
            return 1;
        }

        Run("version, ABI and all status-message exports", () =>
        {
            Require(!string.IsNullOrWhiteSpace(FlukeProtocol.Version), "Missing native version.");
            Equal(1u, FlukeProtocol.AbiVersion);
            foreach (FlukeStatus status in Enum.GetValues<FlukeStatus>())
            {
                Require(!string.IsNullOrWhiteSpace(FlukeProtocol.GetStatusMessage(status)), $"No message for {status}.");
            }
            Require(!string.IsNullOrWhiteSpace(FlukeProtocol.GetStatusMessage((FlukeStatus)int.MaxValue)),
                "Unknown status must still have a diagnostic.");
        });

        Run("identity parser", () =>
        {
            FlukeIdentity identity = FlukeProtocol.ParseIdentity("FLUKE 187,SIM-1.0,SIMULATED");
            AssertSimulatorIdentity(identity);
        });

        (string Payload, double Value, string Unit)[] numericCases =
        [
            ("+1.234 V DC", 1.234, "V DC"),
            ("-12.5 mV DC", -12.5, "mV DC"),
            ("+6.02e-3 mA", 0.00602, "mA"),
            ("-1.25E+2 kOhm", -125, "kOhm"),
            ("0", 0, "")
        ];
        foreach (var fixture in numericCases)
        {
            Run($"display parser preserves value/text/unit: {fixture.Payload}", () =>
            {
                FlukeReading reading = FlukeProtocol.ParseDisplay($"QM,{fixture.Payload}");
                Equal(FlukeReadingState.Value, reading.State);
                Require(reading.Value.HasValue, "Numeric display has no value.");
                Near(fixture.Value, reading.Value.GetValueOrDefault());
                Equal(fixture.Payload, reading.Display);
                Equal(fixture.Unit, reading.Unit);
            });
        }

        Run("C runtime conversion matches nearest-double parsing exactly", () =>
        {
            foreach (string number in new[] { "1.656", "1.676", "1.632", "1.425", "1.423", "0.1", "1.005" })
            {
                FlukeReading reading = FlukeProtocol.ParseDisplay($"QM,+{number} mV AC");
                Equal<double?>(double.Parse(number, CultureInfo.InvariantCulture), reading.Value);
                Equal($"+{number} mV AC", reading.Display);
            }
        });

        foreach (string display in new[] { "Out of Range", "out of range", "OUT OF RANGE" })
        {
            Run($"out-of-range display is not a fake numeric zero: {display}", () =>
            {
                FlukeReading reading = FlukeProtocol.ParseDisplay($"QM,{display}");
                Equal(FlukeReadingState.OutOfRange, reading.State);
                Equal<double?>(null, reading.Value);
                Equal(display, reading.Display);
                Equal("", reading.Unit);
            });
        }
        Run("unknown textual display is preserved", () =>
        {
            FlukeReading reading = FlukeProtocol.ParseDisplay("QM,LEADS");
            Equal(FlukeReadingState.Text, reading.State);
            Equal<double?>(null, reading.Value);
            Equal("LEADS", reading.Display);
        });
        Run("identity parser errors preserve native structured diagnostics", () =>
        {
            AssertNativeError(() => FlukeProtocol.ParseIdentity("not an identity"));
            AssertNativeError(() => FlukeProtocol.ParseIdentity(""));
            AssertNativeError(() => FlukeProtocol.ParseIdentity(
                $"FLUKE 187,{new string('F', 64)},SIMULATED"));
        });
        Run("empty and oversized displays/units fail without truncation", () =>
        {
            AssertNativeError(() => FlukeProtocol.ParseDisplay(""));
            AssertNativeError(() => FlukeProtocol.ParseDisplay("QM,"));
            AssertNativeError(() => FlukeProtocol.ParseDisplay($"QM,{new string('X', 256)}"));
            AssertNativeError(() => FlukeProtocol.ParseDisplay($"QM,1.25 {new string('U', 64)}"));
        });

        Run("open, identity and initial displayed reading", () =>
        {
            using FlukeDevice device = OpenSimulator();
            Equal(FlukeDevice.SimulatorPort, device.Port);
            Require(!device.IsDisposed, "New device is disposed.");
            FlukeReading reading = device.ReadDisplay();
            Equal(FlukeReadingState.Value, reading.State);
            Near(1.234, reading.Value ?? throw new InvalidOperationException("Missing initial numeric value."));
            Equal("+1.234 V DC", reading.Display);
            Equal("V DC", reading.Unit);
            Equal(FlukeDevice.DefaultTimeoutMilliseconds, device.TimeoutMilliseconds);
        });
        Run("extended clock read/set and exact decisecond validation", () =>
        {
            using FlukeDevice device = OpenSimulator();
            uint before = device.ReadClockCounter();
            uint written = device.SetClock(new TimeOnly(12, 34, 0));
            Equal(452400u, written % FlukeDevice.ClockTicksPerDay);
            uint after = device.ReadClockCounter();
            Require(unchecked(after - written) < 100, "Simulator clock readback is inconsistent.");
            Throws<ArgumentOutOfRangeException>(() => device.SetClock(new TimeOnly(0).Add(TimeSpan.FromTicks(1))));
            Require(before < 100, "Initial simulator clock unexpectedly far from its start.");
        });
        Run("calibration and reset confirmations are separate and enforced natively", () =>
        {
            using FlukeDevice device = OpenSimulator();
            FlukeReading before = device.ReadDisplay();
            AssertStatus<FlukeConfirmationRequiredException>(
                () => device.PressButton(FlukeButton.Calibration), FlukeStatus.ConfirmationRequired);
            AssertStatus<FlukeConfirmationRequiredException>(
                () => device.ResetInstrument(), FlukeStatus.ConfirmationRequired);
            Equal(before, device.ReadDisplay());

            // This method can only open the exact simulator URI, never a caller's port.
            device.PressButton(FlukeButton.Calibration, confirmCalibration: true);
            AssertStatus<FlukeConfirmationRequiredException>(
                () => device.ResetInstrument(), FlukeStatus.ConfirmationRequired);
            device.ResetInstrument(confirmReset: true);
            AssertStatus<FlukeConfirmationRequiredException>(
                () => device.PressButton(FlukeButton.Calibration), FlukeStatus.ConfirmationRequired);
        });
        Run("all remaining supported SF buttons and default setup", () =>
        {
            using FlukeDevice device = OpenSimulator();
            foreach (FlukeButton button in Enum.GetValues<FlukeButton>())
            {
                if (button is not (FlukeButton.Calibration or FlukeButton.Logging or FlukeButton.Save))
                {
                    device.PressButton(button);
                }
            }
            device.ApplyDefaultSetup();
            AssertSimulatorIdentity(device.Identity);
        });
        Run("SF23 logging and SF30 save propagate Unsupported", () =>
        {
            using FlukeDevice device = OpenSimulator();
            FlukeReading before = device.ReadDisplay();
            foreach (FlukeButton button in new[] { FlukeButton.Logging, FlukeButton.Save })
            {
                AssertStatus<FlukeUnsupportedException>(() => device.PressButton(button), FlukeStatus.Unsupported);
            }
            Equal(before, device.ReadDisplay());
        });
        Run("timeout get/set, inclusive bounds and managed invalid bounds", () =>
        {
            using FlukeDevice device = OpenSimulator();
            foreach (int timeout in new[] { 50, 1234, 60000, FlukeDevice.DefaultTimeoutMilliseconds })
            {
                device.TimeoutMilliseconds = timeout;
                Equal(timeout, device.TimeoutMilliseconds);
            }
            foreach (int invalid in new[] { int.MinValue, -1, 0, 49, 60001, int.MaxValue })
            {
                Throws<ArgumentOutOfRangeException>(() => device.TimeoutMilliseconds = invalid);
            }
            Equal(FlukeDevice.DefaultTimeoutMilliseconds, device.TimeoutMilliseconds);
            Throws<ArgumentOutOfRangeException>(() => device.PressButton((FlukeButton)24));
        });
        Run("native timeout/button validation, exact confirmation flags and error structures", TestNativeGuards);
        Run("close/dispose is idempotent and all device operations reject use-after-close", TestDisposed);
        Run("independent device handles and timeout isolation", () =>
        {
            using FlukeDevice first = OpenSimulator();
            using FlukeDevice second = OpenSimulator();
            first.TimeoutMilliseconds = 111;
            second.TimeoutMilliseconds = 222;
            Equal(111, first.TimeoutMilliseconds);
            Equal(222, second.TimeoutMilliseconds);
            first.Dispose();
            AssertSimulatorIdentity(second.Identity);
        });

        token.ThrowIfCancellationRequested();
        try
        {
            await TestConcurrentDisposeAsync();
            passed++;
            Console.WriteLine("PASS serialized concurrent operations and close (no stale native handles)");
        }
        catch (Exception exception)
        {
            failed++;
            Console.Error.WriteLine($"FAIL serialized concurrent operations and close: {exception}");
        }
        Run("SafeHandle finalization releases abandoned simulator connections", () =>
        {
            WeakReference abandoned = OpenAndForget();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Require(!abandoned.IsAlive, "An abandoned managed device unexpectedly remained rooted.");
            using FlukeDevice reopened = OpenSimulator();
            AssertSimulatorIdentity(reopened.Identity);
        });

        Console.WriteLine($"Self-test results: {passed} passed, {failed} failed. No hardware ports were opened.");
        return failed == 0 ? 0 : 1;
    }

    private static void TestLayouts()
    {
        Equal(typeof(int), Enum.GetUnderlyingType(typeof(FlukeStatus)));
        Equal(typeof(int), Enum.GetUnderlyingType(typeof(FlukeButton)));
        Equal(typeof(int), Enum.GetUnderlyingType(typeof(FlukeReadingState)));
        Equal(264, Marshal.SizeOf<NativeError>());
        Equal(128, Marshal.SizeOf<NativeIdentity>());
        Equal(176, Marshal.SizeOf<NativeReading>());
        Offset<NativeError>(nameof(NativeError.Status), 0);
        Offset<NativeError>(nameof(NativeError.OsCode), 4);
        Offset<NativeError>(nameof(NativeError.Message), 8);
        Offset<NativeIdentity>(nameof(NativeIdentity.Model), 0);
        Offset<NativeIdentity>(nameof(NativeIdentity.Firmware), 32);
        Offset<NativeIdentity>(nameof(NativeIdentity.SerialNumber), 64);
        Offset<NativeReading>(nameof(NativeReading.Value), 0);
        Offset<NativeReading>(nameof(NativeReading.State), 8);
        Offset<NativeReading>(nameof(NativeReading.Display), 12);
        Offset<NativeReading>(nameof(NativeReading.Unit), 140);
    }

    private static void TestOptions()
    {
        var defaults = ExerciserApp.Options.Parse([]);
        Equal("COM3", defaults.Port); // Parsing a default is not opening a port.
        Equal(TimeSpan.FromSeconds(2), defaults.Interval);
        var bounded = ExerciserApp.Options.Parse(["--port", "sim://187", "--count", "3", "--interval", "0.01"]);
        Equal(3, bounded.Count);
        Equal(TimeSpan.FromMilliseconds(10), bounded.Interval);
        Require(ExerciserApp.Options.Parse(["--self-test"]).SelfTest, "Self-test flag was lost.");
        string[][] invalid =
        [
            ["--self-test", "--port", "sim://187"],
            ["--self-test", "--count", "1"],
            ["--self-test", "--interval", "2"],
            ["--count", "0"], ["--count", "-1"], ["--count", "2147483648"],
            ["--interval", "0"], ["--interval", "-2"], ["--interval", "NaN"],
            ["--interval", "Infinity"], ["--interval", "86401"],
            ["--interval", "1e-100"], ["--count"], ["--port"],
            ["--port", ""], ["--port", "sim://187\0unexpected"],
            ["--unknown"], ["--count", "1", "--count", "2"],
            ["--count", "2147483647", "--interval", "86400"]
        ];
        foreach (string[] arguments in invalid)
        {
            Throws<ArgumentException>(() => ExerciserApp.Options.Parse(arguments));
        }
    }

    private static void TestReadingFormat()
    {
        var timestamp = new DateTimeOffset(2026, 11, 14, 17, 25, 51, TimeSpan.FromHours(2));
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            foreach (string number in new[] { "1.656", "1.676", "1.632", "0.0001234" })
            {
                var reading = new FlukeReading(double.Parse(number, CultureInfo.InvariantCulture),
                    FlukeReadingState.Value, $"+0{number} mV AC", "mV AC");
                Equal($"17:25:51 display=\"{reading.Display}\" unit=\"mV AC\" " +
                    $"state=Value value={number} (displayed units; no scaling)",
                    ExerciserApp.FormatReading(reading, timestamp));
            }
            Equal("17:25:51 display=\"+01.440 mV AC\" unit=\"mV AC\" " +
                "state=Value value=1.44 (displayed units; no scaling)",
                ExerciserApp.FormatReading(
                    new FlukeReading(1.44, FlukeReadingState.Value, "+01.440 mV AC", "mV AC"), timestamp));
            Equal("17:25:51 display=\"Out of Range\" unit=\"\" " +
                "state=OutOfRange value=(no numeric value) (displayed units; no scaling)",
                ExerciserApp.FormatReading(
                    new FlukeReading(null, FlukeReadingState.OutOfRange, "Out of Range", ""), timestamp));
            Equal("fr-FR", CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static void TestManagedGuards()
    {
        Throws<ArgumentNullException>(() => FlukeProtocol.ParseDisplay(null!));
        Throws<ArgumentNullException>(() => FlukeProtocol.ParseIdentity(null!));
        Throws<ArgumentException>(() => FlukeProtocol.ParseDisplay("QM,1\0 V"));
        Throws<ArgumentException>(() => FlukeProtocol.ParseIdentity("FLUKE 187\0,truncated"));
        Throws<ArgumentException>(() => FlukeDevice.Open(""));
        Throws<ArgumentException>(() => FlukeDevice.Open("sim://187\0unexpected"));
        foreach (int timeout in new[] { int.MinValue, -1, 0, 49, 60001, int.MaxValue })
        {
            Throws<ArgumentOutOfRangeException>(() => FlukeDevice.Open(FlukeDevice.SimulatorPort, timeout));
        }
    }

    private static void TestExceptionMappings()
    {
        Type[] expected =
        [
            typeof(FlukeInvalidArgumentException), typeof(FlukeIOException), typeof(FlukeTimeoutException),
            typeof(FlukeProtocolException), typeof(FlukeCommandRejectedException), typeof(FlukeBufferTooSmallException),
            typeof(FlukeWrongModelException), typeof(FlukeConfirmationRequiredException),
            typeof(FlukeUnsupportedException), typeof(FlukeOutOfMemoryException)
        ];
        for (int code = 1; code <= 10; code++)
        {
            FlukeException error = NativeResult.CreateException((FlukeStatus)code, 123, "test operation", "native diagnostic");
            Equal(expected[code - 1], error.GetType());
            Equal((FlukeStatus)code, error.Status);
            Equal(123, error.OperatingSystemErrorCode);
            Equal("test operation", error.Operation);
            Equal("native diagnostic", error.NativeMessage);
            Require(error.Message.Contains("native diagnostic", StringComparison.Ordinal), "Missing diagnostic.");
        }
    }

    private static void TestMalformedErrors()
    {
        Throws<FlukeProtocolException>(() =>
            NativeResult.ThrowIfFailed(2, new NativeError { Status = 3, OsCode = 456 }, "mismatch"));
        Throws<FlukeProtocolException>(() =>
            NativeResult.ThrowIfFailed(999, new NativeError { Status = 999 }, "unknown"));
    }

    private static void TestNativeGuards()
    {
        foreach (uint invalid in new uint[] { 0, 49, 60001, uint.MaxValue })
        {
            int result = NativeMethods.Open(FlukeDevice.SimulatorPort, invalid, out SafeDeviceHandle rejected, out NativeError error);
            using (rejected)
            {
                Require(rejected.IsInvalid, "Failed native open must return NULL.");
                AssertStatus<FlukeInvalidArgumentException>(
                    () => NativeResult.ThrowIfFailed(result, error, "native invalid open timeout"), FlukeStatus.InvalidArgument);
            }
        }

        int opened = NativeMethods.Open(FlukeDevice.SimulatorPort, 2000, out SafeDeviceHandle handle, out NativeError openError);
        using (handle)
        {
            NativeResult.ThrowIfFailed(opened, openError, "open native test simulator");
            Require(!handle.IsInvalid, "Native simulator handle is invalid.");
            foreach (uint invalid in new uint[] { 0, 49, 60001, uint.MaxValue })
            {
                int result = NativeMethods.SetTimeout(handle, invalid, out NativeError error);
                AssertStatus<FlukeInvalidArgumentException>(
                    () => NativeResult.ThrowIfFailed(result, error, "native invalid set timeout"), FlukeStatus.InvalidArgument);
            }
            foreach (int invalid in new[] { int.MinValue, 0, 9, 24, 25, 26, 31, int.MaxValue })
            {
                int result = NativeMethods.PressButton(handle, invalid, 0, out NativeError error);
                AssertStatus<FlukeInvalidArgumentException>(
                    () => NativeResult.ThrowIfFailed(result, error, "native invalid button"), FlukeStatus.InvalidArgument);
            }
            foreach (int confirmation in new[] { -1, 0, 2 })
            {
                int calibration = NativeMethods.PressButton(handle, 20, confirmation, out NativeError calibrationError);
                AssertStatus<FlukeConfirmationRequiredException>(
                    () => NativeResult.ThrowIfFailed(calibration, calibrationError, "native unconfirmed calibration"),
                    FlukeStatus.ConfirmationRequired);
                int reset = NativeMethods.ResetInstrument(handle, confirmation, out NativeError resetError);
                AssertStatus<FlukeConfirmationRequiredException>(
                    () => NativeResult.ThrowIfFailed(reset, resetError, "native unconfirmed reset"),
                    FlukeStatus.ConfirmationRequired);
            }
        }
    }

    private static void TestDisposed()
    {
        FlukeDevice device = OpenSimulator();
        device.Dispose();
        device.Dispose();
        Require(device.IsDisposed, "Disposed state was not reported.");
        Throws<ObjectDisposedException>(() => _ = device.Identity);
        Throws<ObjectDisposedException>(() => device.ReadDisplay());
        Throws<ObjectDisposedException>(() => device.ReadClockCounter());
        Throws<ObjectDisposedException>(() => device.SetClock(new TimeOnly(12, 0)));
        Throws<ObjectDisposedException>(() => _ = device.TimeoutMilliseconds);
        Throws<ObjectDisposedException>(() => device.TimeoutMilliseconds = 100);
        Throws<ObjectDisposedException>(() => device.PressButton(FlukeButton.Hold));
        Throws<ObjectDisposedException>(() => device.PressButton(FlukeButton.Calibration, true));
        Throws<ObjectDisposedException>(() => device.ApplyDefaultSetup());
        Throws<ObjectDisposedException>(() => device.ResetInstrument());
        Throws<ObjectDisposedException>(() => device.ResetInstrument(true));
    }

    private static async Task TestConcurrentDisposeAsync()
    {
        using FlukeDevice device = OpenSimulator();
        using var start = new ManualResetEventSlim(false);
        Task[] workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            start.Wait();
            for (int iteration = 0; iteration < 50; iteration++)
            {
                try
                {
                    if (worker % 2 == 0)
                    {
                        _ = device.ReadDisplay();
                    }
                    else
                    {
                        device.TimeoutMilliseconds = 100 + iteration;
                        _ = device.TimeoutMilliseconds;
                        AssertSimulatorIdentity(device.Identity);
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        })).ToArray();
        Task closer = Task.Run(() =>
        {
            start.Wait();
            device.Dispose();
        });
        start.Set();
        await Task.WhenAll(workers.Append(closer));
        Require(device.IsDisposed, "Concurrent dispose did not complete.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndForget() => new(OpenSimulator());

    private static FlukeDevice OpenSimulator()
    {
        FlukeDevice device = FlukeDevice.Open(FlukeDevice.SimulatorPort);
        try
        {
            AssertSimulatorIdentity(device.Identity);
            return device;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private static void AssertSimulatorIdentity(FlukeIdentity identity)
    {
        Equal("FLUKE 187", identity.Model);
        Equal("SIM-1.0", identity.Firmware);
        Equal("SIMULATED", identity.SerialNumber);
    }

    private static void AssertNativeError(Action action)
    {
        FlukeException exception = Throws<FlukeException>(action);
        Require(exception.Status != FlukeStatus.Ok, "Failure reported success.");
        Require(!string.IsNullOrWhiteSpace(exception.NativeMessage), "Missing native diagnostic.");
        Require(!string.IsNullOrWhiteSpace(exception.Operation), "Missing operation context.");
    }

    private static void AssertStatus<TException>(Action action, FlukeStatus status) where TException : FlukeException
    {
        TException exception = Throws<TException>(action);
        Equal(status, exception.Status);
        Require(!string.IsNullOrWhiteSpace(exception.NativeMessage), "Missing native error message.");
        Require(!string.IsNullOrWhiteSpace(exception.Operation), "Missing operation context.");
    }

    internal static TException Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}, but no matching exception was thrown.");
    }

    private static void Offset<T>(string field, int expected) where T : struct
        => Equal(new IntPtr(expected), Marshal.OffsetOf<T>(field));

    internal static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void Near(double expected, double actual)
        => Require(Math.Abs(expected - actual) <= Math.Max(1, Math.Abs(expected)) * 1e-12,
            $"Expected {expected:G17}, got {actual:G17}.");

    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
