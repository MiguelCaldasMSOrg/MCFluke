using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("MCFluke.Exerciser")]

namespace MCFluke;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeError
{
    internal int Status;
    internal int OsCode;
    internal fixed byte Message[256];

    internal string GetMessage()
    {
        fixed (byte* message = Message)
        {
            return NativeText.Read(message, 256, "native error");
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeIdentity
{
    internal fixed byte Model[32];
    internal fixed byte Firmware[32];
    internal fixed byte SerialNumber[64];

    internal FlukeIdentity ToManaged()
    {
        fixed (byte* model = Model, firmware = Firmware, serial = SerialNumber)
        {
            return new FlukeIdentity(
                NativeText.Read(model, 32, "model"),
                NativeText.Read(firmware, 32, "firmware"),
                NativeText.Read(serial, 64, "serial number"));
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeReading
{
    internal double Value;
    internal int State;
    internal fixed byte Display[128];
    internal fixed byte Unit[32];

    internal FlukeReading ToManaged()
    {
        if (!Enum.IsDefined((FlukeReadingState)State))
        {
            throw new FlukeProtocolException(0, "decode reading", $"Unknown native reading state {State}.");
        }

        var state = (FlukeReadingState)State;
        if (state == FlukeReadingState.Value && !double.IsFinite(Value))
        {
            throw new FlukeProtocolException(0, "decode reading", "A numeric reading was not finite.");
        }

        fixed (byte* display = Display, unit = Unit)
        {
            return new FlukeReading(
                state == FlukeReadingState.Value ? Value : null,
                state,
                NativeText.Read(display, 128, "display"),
                NativeText.Read(unit, 32, "unit"));
        }
    }
}

internal static unsafe class NativeText
{
    internal static string Read(byte* pointer, int capacity, string field)
    {
        var bytes = new ReadOnlySpan<byte>(pointer, capacity);
        int length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            throw new FlukeProtocolException(0, "decode native structure",
                $"The {field} field was not NUL-terminated within {capacity} bytes.");
        }

        return Encoding.UTF8.GetString(bytes[..length]);
    }
}

internal sealed class SafeDeviceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeDeviceHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle()
    {
        NativeMethods.Close(handle);
        return true;
    }
}

internal static class NativeMethods
{
    internal const string LibraryName = "mcfluke";

    static NativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, NativeLibraryLoader.Resolve);
    }

    [DllImport(LibraryName, EntryPoint = "mcfluke_version", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr Version();

    [DllImport(LibraryName, EntryPoint = "mcfluke_abi_version", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint AbiVersion();

    [DllImport(LibraryName, EntryPoint = "mcfluke_status_message", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr StatusMessage(int status);

    [DllImport(LibraryName, EntryPoint = "mcfluke_open", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int Open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string port,
        uint timeoutMilliseconds,
        out SafeDeviceHandle device,
        out NativeError error);

    [DllImport(LibraryName, EntryPoint = "mcfluke_close", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void Close(IntPtr device);

    [DllImport(LibraryName, EntryPoint = "mcfluke_get_identity", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int GetIdentity(SafeDeviceHandle device, out NativeIdentity identity, out NativeError error);

    [DllImport(LibraryName, EntryPoint = "mcfluke_read_display", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ReadDisplay(SafeDeviceHandle device, out NativeReading reading, out NativeError error);

    [DllImport(LibraryName, EntryPoint = "mcfluke_get_clock", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int GetClock(SafeDeviceHandle device, out uint counter, out NativeError error);

    [DllImport(LibraryName, EntryPoint = "mcfluke_set_clock", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SetClock(SafeDeviceHandle device, uint timeOfDayTicks,
        out uint writtenCounter, out NativeError error);

    [DllImport(LibraryName, EntryPoint = "mcfluke_get_timeout", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int GetTimeout(SafeDeviceHandle device, out uint timeoutMilliseconds, out NativeError error);

    [DllImport(LibraryName, EntryPoint = "mcfluke_set_timeout", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SetTimeout(SafeDeviceHandle device, uint timeoutMilliseconds, out NativeError error);

    [DllImport(LibraryName, EntryPoint = "mcfluke_press_button", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int PressButton(SafeDeviceHandle device, int button, int confirmCalibration, out NativeError error);

    [DllImport(LibraryName, EntryPoint = "mcfluke_default_setup", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int DefaultSetup(SafeDeviceHandle device, out NativeError error);

    [DllImport(LibraryName, EntryPoint = "mcfluke_reset_instrument", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ResetInstrument(SafeDeviceHandle device, int confirmation, out NativeError error);

    [DllImport(LibraryName, EntryPoint = "mcfluke_parse_identity", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ParseIdentity(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string response,
        out NativeIdentity identity,
        out NativeError error);

    [DllImport(LibraryName, EntryPoint = "mcfluke_parse_display", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ParseDisplay(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string response,
        out NativeReading reading,
        out NativeError error);
}
