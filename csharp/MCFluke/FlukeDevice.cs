namespace MCFluke;

/// <summary>
/// An exclusively owned connection. Every operation and Dispose are serialized
/// per instance; SafeHandle releases abandoned connections during finalization.
/// </summary>
public sealed class FlukeDevice : IDisposable
{
    public const int MinimumTimeoutMilliseconds = 50;
    public const int MaximumTimeoutMilliseconds = 60000;
    public const int DefaultTimeoutMilliseconds = 2000;
    public const string SimulatorPort = "sim://187";
    public const uint ClockTicksPerSecond = 10;
    public const uint ClockTicksPerDay = 864000;

    private readonly object gate = new();
    private readonly SafeDeviceHandle handle;

    private FlukeDevice(string port, SafeDeviceHandle handle)
    {
        Port = port;
        this.handle = handle;
    }

    public string Port { get; }

    public bool IsDisposed
    {
        get { lock (gate) { return handle.IsClosed; } }
    }

    public static FlukeDevice Open(string port, int timeoutMilliseconds = DefaultTimeoutMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(port);
        FlukeProtocol.ValidateText(port, nameof(port));
        ValidateTimeout(timeoutMilliseconds);
        FlukeProtocol.EnsureCompatibleAbi();

        int result = NativeMethods.Open(port, (uint)timeoutMilliseconds, out SafeDeviceHandle opened, out NativeError error);
        try
        {
            if (result != (int)FlukeStatus.Ok && !opened.IsInvalid)
            {
                throw new FlukeProtocolException(error.OsCode, "open",
                    $"A failed native open returned a non-NULL device (status {result}).");
            }

            NativeResult.ThrowIfFailed(result, error, "open");
            if (opened.IsInvalid)
            {
                throw new FlukeProtocolException(0, "open", "Native open succeeded with an invalid device.");
            }

            return new FlukeDevice(port, opened);
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    public FlukeIdentity Identity
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                int result = NativeMethods.GetIdentity(handle, out NativeIdentity identity, out NativeError error);
                NativeResult.ThrowIfFailed(result, error, "get identity");
                return identity.ToManaged();
            }
        }
    }

    public int TimeoutMilliseconds
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                int result = NativeMethods.GetTimeout(handle, out uint timeout, out NativeError error);
                NativeResult.ThrowIfFailed(result, error, "get timeout");
                if (timeout is < MinimumTimeoutMilliseconds or > MaximumTimeoutMilliseconds)
                {
                    throw new FlukeProtocolException(0, "get timeout", $"Native returned out-of-bounds timeout {timeout}.");
                }
                return (int)timeout;
            }
        }
        set
        {
            lock (gate)
            {
                ThrowIfDisposed();
                ValidateTimeout(value);
                int result = NativeMethods.SetTimeout(handle, (uint)value, out NativeError error);
                NativeResult.ThrowIfFailed(result, error, "set timeout");
            }
        }
    }

    public FlukeReading ReadDisplay()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            int result = NativeMethods.ReadDisplay(handle, out NativeReading reading, out NativeError error);
            NativeResult.ThrowIfFailed(result, error, "read display");
            return reading.ToManaged();
        }
    }

    /// <summary>Reads the running unsigned decisecond counter through the extended 18x QS command.</summary>
    public uint ReadClockCounter()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            int result = NativeMethods.GetClock(handle, out uint counter, out NativeError error);
            NativeResult.ThrowIfFailed(result, error, "read clock counter (QS)");
            return counter;
        }
    }

    /// <summary>
    /// Sets local time-of-day at 0.1-second resolution, preserving all other
    /// settings and the nearest representable counter day. Returns the counter
    /// value sent. The bounded call includes a fresh QS read before the PS write.
    /// </summary>
    public uint SetClock(TimeOnly timeOfDay)
    {
        if (timeOfDay.Ticks % (TimeSpan.TicksPerSecond / ClockTicksPerSecond) != 0)
            throw new ArgumentOutOfRangeException(nameof(timeOfDay), "The clock resolution is 0.1 second.");
        lock (gate)
        {
            ThrowIfDisposed();
            uint ticks = (uint)(timeOfDay.Ticks / (TimeSpan.TicksPerSecond / ClockTicksPerSecond));
            int result = NativeMethods.SetClock(handle, ticks, out uint written, out NativeError error);
            NativeResult.ThrowIfFailed(result, error, "set clock (QS/PS)");
            if (written % ClockTicksPerDay != ticks)
                throw new FlukeProtocolException(0, "set clock", "Native returned a different time-of-day than requested.");
            return written;
        }
    }

    /// <summary>
    /// Presses an SF key. Calibration (SF20) requires an explicit true argument.
    /// It enters a workflow, not an actual or validated calibration.
    /// Logging (SF23) and Save (SF30) are 189-only and throw FlukeUnsupportedException.
    /// </summary>
    public void PressButton(FlukeButton button, bool confirmCalibration = false)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!Enum.IsDefined(button))
            {
                throw new ArgumentOutOfRangeException(nameof(button), button, "Not a defined Fluke SF key.");
            }

            int result = NativeMethods.PressButton(handle, (int)button, confirmCalibration ? 1 : 0, out NativeError error);
            NativeResult.ThrowIfFailed(result, error, $"press {button} (SF{(int)button})");
        }
    }

    /// <summary>Applies the native default setup. This changes meter settings.</summary>
    public void ApplyDefaultSetup()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            int result = NativeMethods.DefaultSetup(handle, out NativeError error);
            NativeResult.ThrowIfFailed(result, error, "default setup");
        }
    }

    /// <summary>
    /// RI erases user settings and resets the clock. Requires an explicit true
    /// argument, independently of calibration confirmation. Does not reset calibration constants.
    /// </summary>
    public void ResetInstrument(bool confirmReset = false)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            int result = NativeMethods.ResetInstrument(handle, confirmReset ? 1 : 0, out NativeError error);
            NativeResult.ThrowIfFailed(result, error, "reset instrument (RI)");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            handle.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(handle.IsClosed, this);

    private static void ValidateTimeout(int value)
    {
        if (value is < MinimumTimeoutMilliseconds or > MaximumTimeoutMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"Timeout must be {MinimumTimeoutMilliseconds}..{MaximumTimeoutMilliseconds} milliseconds.");
        }
    }
}
