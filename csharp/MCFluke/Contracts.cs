namespace MCFluke;

public enum FlukeStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    IoError = 2,
    Timeout = 3,
    ProtocolError = 4,
    CommandRejected = 5,
    BufferTooSmall = 6,
    WrongModel = 7,
    ConfirmationRequired = 8,
    Unsupported = 9,
    OutOfMemory = 10
}

public enum FlukeButton : int
{
    Blue = 10,
    Hold = 11,
    MinMax = 12,
    Relative = 13,
    Up = 14,
    Shift = 15,
    Hertz = 16,
    Range = 17,
    Down = 18,
    Backlight = 19,
    Calibration = 20,
    AutoHold = 21,
    FastMinMax = 22,
    Logging = 23,
    Cancel = 27,
    Wakeup = 28,
    Setup = 29,
    Save = 30
}

public enum FlukeReadingState : int
{
    Value = 0,
    OutOfRange = 1,
    Text = 2
}

public sealed record FlukeIdentity(string Model, string Firmware, string SerialNumber);

/// <summary>
/// The meter's exact display and unit. Value is null for nonnumeric states and
/// otherwise remains in the displayed units: no SI scaling is performed.
/// </summary>
public sealed record FlukeReading(
    double? Value,
    FlukeReadingState State,
    string Display,
    string Unit);
