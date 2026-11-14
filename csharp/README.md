# MCFluke C# binding and exerciser

Original, dependency-free .NET 10 code for the immutable ABI 1 contract in
`include\mcfluke.h`. No GPL/sigrok implementation is incorporated.

## Build and run

From the repository root in PowerShell, with .NET SDK 10.0.404 or later:

```powershell
dotnet build .\csharp\MCFluke.Exerciser\MCFluke.Exerciser.csproj
dotnet run --project .\csharp\MCFluke.Exerciser -- --help
dotnet run --project .\csharp\MCFluke.Exerciser -- --self-test
dotnet run --project .\csharp\MCFluke.Exerciser -- --port sim://187
```

The self-test needs the native CMake library but **no hardware**. It always opens
exactly `sim://187`; it rejects `--port`, `--count`, and `--interval` alongside
`--self-test`. Simulation is never a fallback for a failed hardware connection.

After building the native CMake target, the binding discovers the native library
in the application output directory or `build\bin` beneath any ancestor of that
directory. It does **not** rely on the current working directory. Expected names:
`mcfluke.dll` on Windows, `libmcfluke.so` on Linux, `libmcfluke.dylib` on macOS.
For publishing, running outside the repository, or choosing a specific build:

```powershell
$env:MCFLUKE_LIBRARY = 'C:\Projects\MCFluke\build\bin\mcfluke.dll'
dotnet run --project .\csharp\MCFluke.Exerciser -- --self-test
```

The override must be an absolute **file** path; a broken override fails rather
than silently loading another library. Native loading errors include diagnostics.
There are no NuGet package dependencies. Both projects single-target `net10.0`
with nullable analysis and warnings treated as errors.
The managed assembly is named `MCFluke.Managed.dll` to avoid colliding with
native `mcfluke.dll` on case-insensitive Windows filesystems.

To create the version `0.1.0` managed-only NuGet package, including this README:

```powershell
dotnet pack .\csharp\MCFluke\MCFluke.csproj --configuration Release --output .\out\packages
```

The native library is built and supplied separately; it is not bundled in the
NuGet package. No package license has been selected or declared.

## Real hardware: explicit, read-only bounded mode

Only run these commands when the specified meter and port are intended:

```powershell
dotnet run --project .\csharp\MCFluke.Exerciser -- --port COM3 --count 5 --interval 2
```

`--port` defaults to `COM3`; serial configuration is handled by native code
(9600 baud, 8N1, no handshake). Bounded mode performs only connection/ID
verification, identity retrieval, and the requested display reads. It never
presses keys or invokes setup/reset/calibration. The default read cadence is
two seconds; `--interval` accepts invariant-culture seconds. Reads run on
scheduled start times; late reads are performed without a negative delay.

Reading timestamps use the **PC's local clock**, formatted as `HH:mm:ss` when
the reading is printed. They do not come from the Fluke. Numeric values use
concise invariant formatting rather than forced 17-digit output; `Display`
still preserves every character sent by the meter, including trailing zeros.

Menu **13** offers clock synchronization after typed **SYNC**. It measures
command round trips, targets the next feasible 0.1-second boundary, and
corrects launch timing using decisecond clock readbacks. It stops after the
first satisfactory result and makes at most four writes only if needed.
It normally takes seconds, not minutes; a 30-second budget is checked between
I/O calls, which retain their configured timeout. This is an experimental
community-evidenced QS/PS feature, not a manufacturer-certified timing
operation; [details and uncertainty](../docs/clock-sync.md) are documented.

Without `--count`, the interactive menu exposes **all** capabilities: identity,
single/repeated display reads, every valid SF key, default setup, RI reset,
timeout get/set, both parsers, version, ABI, and status messages. No control
operation is automatic:

- Default setup changes settings and requires typed `SETUP`.
- RI erases user settings and resets the clock; it requires typed `RESET`.
- SF20 independently requires typed `CALIBRATION`. It enters the calibration
  workflow; it does not perform or validate calibration.
- SF23 (logging) and SF30 (save) are 189-only. Their native `Unsupported` errors
  are displayed, not swallowed.
- No operation resets calibration constants.

Confirmation tokens must match exactly. EOF abandons pending prompts without a
command; Ctrl+C closes the connection after any in-flight native operation
finishes. Exit codes: `0` success/EOF, `1` native or test failure (also retained
if an interactive operation failed), `2` invalid arguments, `130` cancellation.

## Library use

```csharp
using MCFluke;

Console.WriteLine($"{FlukeProtocol.Version}, ABI {FlukeProtocol.AbiVersion}");
using FlukeDevice meter = FlukeDevice.Open("sim://187", timeoutMilliseconds: 2000);
FlukeIdentity identity = meter.Identity;
FlukeReading reading = meter.ReadDisplay();
Console.WriteLine($"{identity.Model}: {reading.Display} [{reading.Unit}]");
if (reading.Value is double value)
    Console.WriteLine($"Displayed-unit value: {value}");
meter.TimeoutMilliseconds = 1500; // Inclusive native limits: 50..60000 ms.
meter.PressButton(FlukeButton.Hold); // Only when explicitly intended.

FlukeReading parsed = FlukeProtocol.ParseDisplay("QM,-12.5 mV DC");
// parsed.Value == -12.5, not -0.0125; display and unit are preserved.
```

Out-of-range and unknown textual readings expose `Value == null` and an explicit
`FlukeReadingState`, never a fabricated numeric zero. `Display` and `Unit`
retain the native text; no SI conversion occurs. Calibration and RI are separate
APIs/confirmation arguments:
`PressButton(FlukeButton.Calibration, confirmCalibration: true)` and
`ResetInstrument(confirmReset: true)`. Omitted confirmation is rejected by the
native guard. Library callers must obtain explicit consent before passing true.

All per-device operations, including timeout changes and disposal, share one
lock. Separate devices operate independently. The opaque handle is private;
`SafeHandle` provides finalization if a caller forgets disposal, and P/Invoke
holds a safe reference during calls. Operations after disposal throw
`ObjectDisposedException`; repeated disposal is harmless. ABI compatibility is
checked before passing any fixed native structure. Native enums/statuses use
signed 32-bit integers; timeout/ABI use unsigned 32-bit integers; all calls are
Cdecl with bounded fixed-byte structures matching the C header.

`ReadClockCounter()` returns the full unsigned decisecond counter.
`SetClock(TimeOnly)` accepts exact 0.1-second increments, reads fresh settings
and changes only the clock field, preserving the nearest counter day. It
returns the raw counter written. This immediate setter is distinct from
menu 13's measured, decisecond-boundary workflow. Its QS read and PS write share
one native operation timeout. Existing methods/signatures are unchanged.

Failures are typed `FlukeException` subclasses for all ten native error statuses
(including timeout, I/O, protocol, wrong model, unsupported, command rejection,
confirmation required, buffer-too-small and allocation failure). Each carries
`Status`, `OperatingSystemErrorCode`, `Operation`, and `NativeMessage`. A mismatch
between the return status and structured error, or an invalid native reading,
is a `FlukeProtocolException`; incompatible ABI is a `FlukeAbiException`.
Managed argument errors use ordinary .NET argument exceptions.

## Offline test coverage

`--self-test` is an executable test runner with no external test packages.
It validates native struct sizes/offsets and enum widths; argument/CLI guards;
all typed error mappings and structured status validation; every exported native
function; simulator identity (`FLUKE 187`, `SIM-1.0`, `SIMULATED`) and initial
`+1.234 V DC` display; numeric/negative/exponent/unit parsing without scaling;
out-of-range/text/null/NUL/oversized parsing edges; every valid button; separate
calibration/reset confirmation guards (including flags other than exactly 1);
189-only errors; default setup; timeout boundaries in both layers; independent
devices; use-after-close, repeated disposal, concurrent calls/disposal, and
SafeHandle finalization. Confirmation is acknowledged automatically **only**
inside the explicitly selected native simulator.

Missing native code reports native coverage as **BLOCKED**, exits nonzero, and
does not touch a serial port. These tests do not claim to validate physical
meter behavior, real serial timeout timing, or calibration accuracy.
