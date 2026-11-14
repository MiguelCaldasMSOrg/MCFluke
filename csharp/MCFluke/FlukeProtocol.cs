using System.Runtime.InteropServices;

namespace MCFluke;

/// <summary>Stateless native protocol helpers. None of these methods opens a port.</summary>
public static class FlukeProtocol
{
    public const uint SupportedAbiVersion = 1;
    private static readonly Lazy<bool> AbiCheck = new(() =>
    {
        uint actual = AbiVersion;
        if (actual != SupportedAbiVersion)
        {
            throw new FlukeAbiException(actual);
        }
        return true;
    });

    public static string Version => ReadStaticString(NativeMethods.Version(), "version");
    public static uint AbiVersion => NativeMethods.AbiVersion();

    public static void EnsureCompatibleAbi() => _ = AbiCheck.Value;

    public static string GetStatusMessage(FlukeStatus status)
        => ReadStaticString(NativeMethods.StatusMessage((int)status), "status message");

    public static FlukeIdentity ParseIdentity(string response)
    {
        ValidateText(response, nameof(response));
        EnsureCompatibleAbi();
        int result = NativeMethods.ParseIdentity(response, out NativeIdentity identity, out NativeError error);
        NativeResult.ThrowIfFailed(result, error, "parse identity");
        return identity.ToManaged();
    }

    public static FlukeReading ParseDisplay(string response)
    {
        ValidateText(response, nameof(response));
        EnsureCompatibleAbi();
        int result = NativeMethods.ParseDisplay(response, out NativeReading reading, out NativeError error);
        NativeResult.ThrowIfFailed(result, error, "parse display");
        return reading.ToManaged();
    }

    internal static void ValidateText(string text, string parameter)
    {
        ArgumentNullException.ThrowIfNull(text, parameter);
        if (text.Contains('\0'))
        {
            throw new ArgumentException("Embedded NUL characters are not permitted in C strings.", parameter);
        }
    }

    private static string ReadStaticString(IntPtr pointer, string description)
        => pointer == IntPtr.Zero
            ? throw new FlukeProtocolException(0, $"read {description}", "Native returned a NULL string.")
            : Marshal.PtrToStringUTF8(pointer)
                ?? throw new FlukeProtocolException(0, $"read {description}", "Native string could not be decoded.");
}
