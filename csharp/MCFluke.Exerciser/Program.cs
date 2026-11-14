using System.Globalization;
using MCFluke;
using MCFluke.Exerciser;

return await ExerciserApp.RunAsync(args);

internal static class ExerciserApp
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            Options options = Options.Parse(arguments);
            if (options.Help)
            {
                PrintHelp();
                return 0;
            }

            if (options.SelfTest)
            {
                return await SelfTests.RunAsync(cancellation.Token);
            }

            PrintVersion();
            Console.WriteLine($"Opening {options.Port}; only ID verification occurs automatically.");
            using FlukeDevice device = FlukeDevice.Open(options.Port);
            PrintIdentity(device.Identity);
            if (options.Count is int count)
            {
                Console.WriteLine("Bounded READ-ONLY mode: no buttons, setup, reset, or calibration commands.");
                await ReadRepeatedlyAsync(device, count, options.Interval, cancellation.Token);
                return 0;
            }

            return await InteractiveAsync(device, options.Interval, cancellation.Token);
        }
        catch (EndOfInputException)
        {
            Console.WriteLine("\nEnd of input; connection closed.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("\nCancelled; connection closed.");
            return 130;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid argument: {exception.Message}\nUse --help for usage.");
            return 2;
        }
        catch (Exception exception)
        {
            PrintError(exception);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static async Task<int> InteractiveAsync(FlukeDevice device, TimeSpan interval, CancellationToken token)
    {
        bool hadError = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Console.WriteLine("""

                1  Identity                 2  Read displayed value once
                3  Repeated reads           4  Choose a button (SF)
                5  Default setup (changes settings)
                6  Factory/instrument reset (RI; DESTRUCTIVE)
                7  Get timeout              8  Set timeout
                9  Parse an ID payload     10  Parse a full QM display line
               11  Version and ABI        12  Status messages
               13  Synchronize Fluke clock (about 3-4 minutes)
                h  Help                    q  Quit
               """);
            string? choice = await ReadLineAsync("Choice: ", token);
            if (choice is null || choice.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
            {
                return hadError ? 1 : 0;
            }

            try
            {
                switch (choice.Trim().ToLowerInvariant())
                {
                    case "1":
                        PrintIdentity(device.Identity);
                        break;
                    case "2":
                        PrintReading(device.ReadDisplay());
                        break;
                    case "3":
                        string countText = await RequiredLineAsync("Read count (positive integer, default 10): ", token);
                        int count = string.IsNullOrWhiteSpace(countText) ? 10 : ParsePositiveInt(countText, "read count");
                        await ReadRepeatedlyAsync(device, count, interval, token);
                        break;
                    case "4":
                        await ChooseButtonAsync(device, token);
                        break;
                    case "5":
                        Console.WriteLine("WARNING: default setup changes meter settings; it is not a read-only operation.");
                        if (await ConfirmAsync("Type SETUP to apply default setup: ", "SETUP", token))
                        {
                            device.ApplyDefaultSetup();
                            Console.WriteLine("Default setup applied.");
                        }
                        break;
                    case "6":
                        Console.WriteLine("WARNING: RI erases user settings and resets the clock. Calibration constants are not reset.");
                        if (await ConfirmAsync("Type RESET to reset the instrument: ", "RESET", token))
                        {
                            device.ResetInstrument(confirmReset: true);
                            Console.WriteLine("Instrument reset.");
                        }
                        break;
                    case "7":
                        Console.WriteLine($"Timeout: {device.TimeoutMilliseconds} ms");
                        break;
                    case "8":
                        int timeout = ParsePositiveInt(
                            await RequiredLineAsync("Timeout in milliseconds (50..60000): ", token), "timeout");
                        device.TimeoutMilliseconds = timeout;
                        Console.WriteLine($"Timeout now {device.TimeoutMilliseconds} ms");
                        break;
                    case "9":
                        PrintIdentity(FlukeProtocol.ParseIdentity(
                            await RequiredLineAsync("ID payload (for example FLUKE 187,SIM-1.0,SIMULATED): ", token)));
                        break;
                    case "10":
                        PrintReading(FlukeProtocol.ParseDisplay(
                            await RequiredLineAsync("Full display line (for example QM,+1.234 V DC): ", token)));
                        break;
                    case "11":
                        PrintVersion();
                        break;
                    case "12":
                        foreach (FlukeStatus status in Enum.GetValues<FlukeStatus>())
                        {
                            Console.WriteLine($"{(int)status,2} {status,-22} {FlukeProtocol.GetStatusMessage(status)}");
                        }
                        break;
                    case "13":
                        Console.WriteLine("Experimental 18x QS/PS clock synchronization; other settings are preserved by read-modify-write.");
                        Console.WriteLine("It performs three timed minute-boundary writes, or a fourth if needed, and checks clock readback.");
                        Console.WriteLine("Keep the meter awake and optical head fixed; do not change settings or press meter buttons during synchronization.");
                        Console.WriteLine("Subsecond results are measured estimates: the exact firmware latch/divider behavior is not manufacturer-specified.");
                        if (await ConfirmAsync("Type SYNC to synchronize to the PC's local clock: ", "SYNC", token))
                        {
                            await ClockSynchronization.RunAsync(new FlukeClockSyncMeter(device),
                                new SystemClockSyncTime(), Console.WriteLine, token);
                        }
                        break;
                    case "h":
                    case "help":
                        PrintHelp();
                        break;
                    default:
                        Console.WriteLine("Unknown choice; use the numbers shown, h, or q.");
                        break;
                }
            }
            catch (Exception exception) when (exception is FlukeException or ArgumentException or ClockSyncException)
            {
                hadError = true;
                PrintError(exception);
            }
            catch (EndOfInputException)
            {
                Console.WriteLine("\nEnd of input; no pending command sent.");
                return hadError ? 1 : 0;
            }
        }
    }

    private static async Task ChooseButtonAsync(FlukeDevice device, CancellationToken token)
    {
        Console.WriteLine("No key is sent until you explicitly select it. Keys can be rejected in the meter's current mode.");
        foreach (FlukeButton button in Enum.GetValues<FlukeButton>())
        {
            string caution = button switch
            {
                FlukeButton.Calibration => " -- DANGEROUS: enters calibration workflow; requires CALIBRATION",
                FlukeButton.Logging or FlukeButton.Save => " -- 189-only: Unsupported on 187",
                _ => ""
            };
            Console.WriteLine($"SF{(int)button,2}: {button}{caution}");
        }

        string input = await RequiredLineAsync("SF number, or b to return: ", token);
        if (input.Trim().Equals("b", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int number = ParsePositiveInt(input, "button number");
        var selected = (FlukeButton)number;
        if (!Enum.IsDefined(selected))
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Select one of the listed SF numbers.");
        }

        bool confirmed = false;
        if (selected == FlukeButton.Calibration)
        {
            Console.WriteLine("WARNING: SF20 enters the meter's calibration workflow. It does NOT perform or validate calibration.");
            confirmed = await ConfirmAsync("Type CALIBRATION to enter that workflow: ", "CALIBRATION", token);
            if (!confirmed)
            {
                return;
            }
        }

        token.ThrowIfCancellationRequested();
        device.PressButton(selected, confirmCalibration: confirmed);
        Console.WriteLine($"Sent {selected} (SF{number}).");
    }

    private static async Task ReadRepeatedlyAsync(FlukeDevice device, int count, TimeSpan interval, CancellationToken token)
    {
        Console.WriteLine($"{count} reads, {interval.TotalSeconds.ToString("G", CultureInfo.InvariantCulture)} s cadence; Ctrl+C exits.");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            if (index > 0)
            {
                TimeSpan remaining = TimeSpan.FromTicks(checked(interval.Ticks * (long)index)) - clock.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, token);
                }
            }
            PrintReading(device.ReadDisplay());
        }
    }

    private static void PrintIdentity(FlukeIdentity identity)
        => Console.WriteLine($"Model: {identity.Model}; firmware: {identity.Firmware}; serial: {identity.SerialNumber}");

    private static void PrintReading(FlukeReading reading)
        => Console.WriteLine(FormatReading(reading, DateTimeOffset.Now));

    internal static string FormatReading(FlukeReading reading, DateTimeOffset timestamp)
    {
        string value = reading.Value?.ToString("G", CultureInfo.InvariantCulture) ?? "(no numeric value)";
        string time = timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return $"{time} display=\"{reading.Display}\" unit=\"{reading.Unit}\" " +
            $"state={reading.State} value={value} (displayed units; no scaling)";
    }

    private static void PrintVersion()
    {
        Console.WriteLine($"Native version: {FlukeProtocol.Version}; native ABI: {FlukeProtocol.AbiVersion}; " +
            $"binding ABI: {FlukeProtocol.SupportedAbiVersion}");
        FlukeProtocol.EnsureCompatibleAbi();
    }

    private static void PrintError(Exception exception)
    {
        if (exception is TypeInitializationException { InnerException: { } inner })
        {
            PrintError(inner);
            return;
        }
        Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
    }

    private static async Task<string?> ReadLineAsync(string prompt, CancellationToken token)
    {
        Console.Write(prompt);
        return await Console.In.ReadLineAsync().WaitAsync(token);
    }

    private static async Task<string> RequiredLineAsync(string prompt, CancellationToken token)
        => await ReadLineAsync(prompt, token) ?? throw new EndOfInputException();

    private static async Task<bool> ConfirmAsync(string prompt, string required, CancellationToken token)
    {
        string response = await RequiredLineAsync(prompt, token);
        token.ThrowIfCancellationRequested();
        if (string.Equals(response, required, StringComparison.Ordinal))
        {
            return true;
        }
        Console.WriteLine("Not confirmed; no command sent.");
        return false;
    }

    internal static int ParsePositiveInt(string input, string name)
        => int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : throw new ArgumentException($"{name} must be a positive 32-bit integer.");

    private static void PrintHelp() => Console.WriteLine("""
        MCFluke C# full exerciser (.NET 10; native ABI 1)

        dotnet run --project csharp\MCFluke.Exerciser -- --help
        dotnet run --project csharp\MCFluke.Exerciser -- --self-test
        dotnet run --project csharp\MCFluke.Exerciser -- --port sim://187
        dotnet run --project csharp\MCFluke.Exerciser -- --port COM3 --count 5 --interval 2

        --port PORT       Serial port; default COM3. Only sim://187 explicitly selects simulation.
        --count N         Bounded READ-ONLY mode: N reads, no control commands.
        --interval SEC    Read cadence in seconds (default 2; positive, at most 86400).
        --self-test       Offline native simulator tests ONLY; cannot combine with port/count/interval.
        --help, -h        This help; does not load native code or open a port.

        Native discovery: absolute MCFLUKE_LIBRARY override, application directory, then
        ancestor build\bin directories. Build native code first; no simulator fallback.
        The platform names are mcfluke.dll, libmcfluke.so, and libmcfluke.dylib.

        Interactive mode covers identity, readings, every SF button, default setup, RI,
        timeout get/set, both parsers, version, ABI, and every native status message.
        Controls are NEVER automatic. Default setup requires SETUP; RI erases settings
        and the clock and requires RESET. SF20 separately requires CALIBRATION and only
        enters a calibration workflow. SF23 logging / SF30 save are 189-only and report
        Unsupported on the 187. No operation resets calibration constants.
        Numeric values and units match the display exactly; no SI conversion.
        Reading timestamps use the PC's local clock (HH:mm:ss), not the meter's clock.
        Menu 13 synchronizes the meter clock through extended QS/PS commands after typed SYNC.
        It measures latency, compensates timed minute-boundary writes, and verifies counter readback.
        Clock synchronization usually takes 3-4 minutes. Keep the optical link stable and do not operate the meter.
        Ctrl+C exits (130); EOF exits without sending the pending command.
        Exit codes: 0 success, 1 operation/test/native-load failure, 2 invalid CLI input.
        """);

    private sealed class EndOfInputException : Exception;

    internal sealed record Options(bool Help, bool SelfTest, string Port, int? Count, TimeSpan Interval)
    {
        internal static Options Parse(string[] arguments)
        {
            bool help = false, selfTest = false, portSet = false, intervalSet = false;
            string port = "COM3";
            int? count = null;
            TimeSpan interval = TimeSpan.FromSeconds(2);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < arguments.Length; index++)
            {
                string argument = arguments[index];
                string canonical = argument == "-h" ? "--help" : argument;
                if (!seen.Add(canonical))
                {
                    throw new ArgumentException($"Repeated argument: {argument}");
                }

                string NextValue()
                    => ++index < arguments.Length ? arguments[index]
                        : throw new ArgumentException($"{argument} requires a value.");

                switch (canonical)
                {
                    case "--help": help = true; break;
                    case "--self-test": selfTest = true; break;
                    case "--port":
                        port = NextValue();
                        ArgumentException.ThrowIfNullOrWhiteSpace(port);
                        FlukeProtocol.ValidateText(port, nameof(port));
                        portSet = true;
                        break;
                    case "--count":
                        count = ParsePositiveInt(NextValue(), "--count");
                        break;
                    case "--interval":
                        if (!double.TryParse(NextValue(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                            || !double.IsFinite(seconds) || seconds <= 0 || seconds > 86400)
                        {
                            throw new ArgumentException("--interval must be finite and greater than 0, at most 86400 seconds.");
                        }
                        interval = TimeSpan.FromSeconds(seconds);
                        if (interval <= TimeSpan.Zero)
                        {
                            throw new ArgumentException("--interval must be at least one TimeSpan tick (100 ns).");
                        }
                        intervalSet = true;
                        break;
                    default: throw new ArgumentException($"Unknown argument: {argument}");
                }
            }

            if (selfTest && (portSet || count is not null || intervalSet))
            {
                throw new ArgumentException("--self-test cannot be combined with --port, --count, or --interval; it uses ONLY sim://187.");
            }
            if (count is int readCount && interval.Ticks > TimeSpan.MaxValue.Ticks / readCount)
            {
                throw new ArgumentException("The requested count and interval exceed the supported total duration.");
            }
            return new Options(help, selfTest, port, count, interval);
        }
    }
}
