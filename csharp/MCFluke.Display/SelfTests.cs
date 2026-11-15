using System.Diagnostics;

namespace MCFluke.Display;

internal static class SelfTests
{
    internal static int Run()
    {
        int passed = 0;
        int failed = 0;
        void Run(string name, Action test)
        {
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

        Console.WriteLine("Display self-tests: only sim://187 is opened; no hardware port is used.");
        Run("explicit port selection and isolated self-test arguments", () =>
        {
            Require(Program.ParsePort([]) == "COM3", "Default port changed.");
            Require(Program.ParsePort(["--port", FlukeDevice.SimulatorPort]) == FlukeDevice.SimulatorPort,
                "Explicit simulator port changed.");
            foreach (string[] invalid in new string[][]
            {
                ["--port"], ["--port", ""], ["--port", " "], ["--unknown"],
                ["--self-test", "--port", "COM3"], ["--port", "COM3", "--port", "COM4"]
            })
            {
                Throws<ArgumentException>(() => Program.ParsePort(invalid));
            }
        });
        Run("one-third-screen bounds and centering include secondary monitor origins", () =>
        {
            Require(MeasurementWindow.InitialBounds(new Rectangle(0, 0, 1920, 1080)) ==
                new Rectangle(640, 360, 640, 360), "Primary monitor bounds are wrong.");
            Require(MeasurementWindow.InitialBounds(new Rectangle(-2560, 120, 2560, 1440)) ==
                new Rectangle(-1707, 600, 853, 480), "Secondary monitor bounds are wrong.");
        });
        Run("display precision, signs, exponents, states and units are preserved", () =>
        {
            var cases = new (FlukeReading Reading, MeasurementText Expected)[]
            {
                (new(1.23, FlukeReadingState.Value, "  +001.2300mV DC  ", "mV DC"), new("+001.2300", "mV DC")),
                (new(-0.00602, FlukeReadingState.Value, "-6.020e-3 mA", "mA"), new("-6.020e-3", "mA")),
                (new(0, FlukeReadingState.Value, "0", ""), new("0", "")),
                (new(null, FlukeReadingState.OutOfRange, "Out of Range. V AC", "V AC"),
                    new("Out of Range.", "V AC")),
                (new(null, FlukeReadingState.Text, "LEADS", ""), new("LEADS", ""))
            };
            foreach (var fixture in cases)
            {
                Require(MeasurementText.FromReading(fixture.Reading) == fixture.Expected,
                    $"Incorrect display split for {fixture.Reading.Display}.");
            }
        });
        Run("fitted value and half-size baseline-aligned units stay centered at different aspect ratios", TestLayout);
        Run("parameterless designer construction needs no meter or port", () =>
        {
            using var window = new MeasurementWindow();
            Require(window.ClientSize == new Size(640, 360) && window.ExitCode == 0,
                "The designer construction path did not initialize the form.");
        });
        Run("only the resize frame remains, with no child controls", () =>
        {
            using var window = new InputWindow();
            Require((window.WindowStyle & 0x00C00000) == 0, "A title bar or caption border remains.");
            Require((window.WindowStyle & 0x00040000) != 0, "The resize frame is missing.");
            Require(!window.ControlBox && !window.MinimizeBox && !window.MaximizeBox && window.Controls.Count == 0,
                "Unexpected window controls.");
        });
        foreach (MouseButtons button in new[]
        {
            MouseButtons.Left, MouseButtons.Right, MouseButtons.Middle, MouseButtons.XButton1, MouseButtons.XButton2
        })
        {
            Run($"first {button} mouse press closes the window", () =>
            {
                using var window = new InputWindow();
                _ = window.Handle;
                window.PressMouse(button);
                Require(window.IsDisposed, "The first mouse press did not close the window.");
            });
        }
        Run("first touch/pointer press closes the window", () =>
        {
            using var window = new InputWindow();
            window.PressPointer();
            Require(window.IsDisposed, "The first pointer press did not close the window.");
        });
        Run("continuous simulator polling has no fixed sample delay and honors cancellation", () =>
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int count = 0;
            var elapsed = Stopwatch.StartNew();
            Throws<OperationCanceledException>(() => MeasurementReader.ReadAsync(FlukeDevice.SimulatorPort, (reading, _) =>
            {
                Require(MeasurementText.FromReading(reading) == new MeasurementText("+1.234", "V DC"),
                    "Simulator reading changed or a reading was fabricated.");
                if (++count == 20)
                {
                    stop.Cancel();
                }
                return Task.CompletedTask;
            }, stop.Token).GetAwaiter().GetResult());
            Require(count == 20 && elapsed.Elapsed < TimeSpan.FromSeconds(5),
                $"Only {count} samples completed in {elapsed.Elapsed}; an artificial sample delay may be present.");
        });
        Run("cancellation prevents publishing any more readings", () =>
        {
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            Throws<OperationCanceledException>(() => MeasurementReader.ReadAsync(FlukeDevice.SimulatorPort,
                (_, _) => throw new InvalidOperationException("A cancelled reader published a reading."),
                stop.Token).GetAwaiter().GetResult());
        });
        Run("asynchronous publication failures are observed by the reader's caller", () =>
        {
            var failure = new InvalidOperationException("Expected publication failure.");
            InvalidOperationException observed = Throws<InvalidOperationException>(() =>
                MeasurementReader.ReadAsync(FlukeDevice.SimulatorPort, (_, _) => Task.FromException(failure),
                    CancellationToken.None).GetAwaiter().GetResult());
            Require(ReferenceEquals(failure, observed), "The original publication failure was lost.");
        });

        Console.WriteLine($"Display results: {passed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static void TestLayout()
    {
        using var bitmap = new Bitmap(1600, 900);
        using Graphics graphics = Graphics.FromImage(bitmap);
        using var family = new FontFamily("Segoe UI");
        foreach (MeasurementText text in new[]
        {
            new MeasurementText("+1.234", "V DC"), new("-1234.000", "mV AC"),
            new("Out of Range.", "kOhm"), new("LEADS", "")
        })
        {
            foreach (Size size in new[] { new Size(640, 360), new(200, 900), new(1600, 180), new(48, 24) })
            {
                var available = new RectangleF(8, 8, size.Width, size.Height);
                using MeasurementLayout layout = MeasurementLayout.Fit(graphics, family, text, available);
                Require(layout.Bounds.Left >= available.Left - 0.01f &&
                    layout.Bounds.Top >= available.Top - 0.01f &&
                    layout.Bounds.Right <= available.Right + 0.01f &&
                    layout.Bounds.Bottom <= available.Bottom + 0.01f, "Reading does not fit.");
                Require(Math.Abs(layout.Bounds.Left + layout.Bounds.Width / 2 -
                    (available.Left + available.Width / 2)) < 0.01f, "Reading is not horizontally centered.");
                Require(Math.Abs(layout.Bounds.Top + layout.Bounds.Height / 2 -
                    (available.Top + available.Height / 2)) < 0.01f, "Reading is not vertically centered.");
                Require(Math.Abs(layout.ValueFont.Size / 2 - layout.UnitFont.Size) < 0.001f,
                    "Units are not exactly half the value font size.");
                float ascent = (float)family.GetCellAscent(FontStyle.Regular) / family.GetEmHeight(FontStyle.Regular);
                Require(Math.Abs(layout.ValueOrigin.Y + layout.ValueFont.Size * ascent -
                    layout.UnitOrigin.Y - layout.UnitFont.Size * ascent) < 0.01f, "Text baselines differ.");
                layout.Draw(graphics, text, Brushes.White);
            }
        }
        var reading = new MeasurementText("+1.234", "V DC");
        using MeasurementLayout small = MeasurementLayout.Fit(graphics, family, reading, new RectangleF(0, 0, 320, 180));
        using MeasurementLayout large = MeasurementLayout.Fit(graphics, family, reading, new RectangleF(0, 0, 640, 360));
        Require(large.ValueFont.Size > small.ValueFont.Size * 1.99f,
            "The font does not grow to use the resized window.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class InputWindow() : MeasurementWindow(FlukeDevice.SimulatorPort)
    {
        internal int WindowStyle => CreateParams.Style;
        internal void PressMouse(MouseButtons button) => OnMouseDown(new MouseEventArgs(button, 1, 20, 20, 0));

        internal void PressPointer()
        {
            Message message = Message.Create(Handle, 0x0246, IntPtr.Zero, IntPtr.Zero);
            WndProc(ref message);
        }
    }
}
