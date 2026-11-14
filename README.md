# MCFluke

Plain-C access to the **Fluke 187** optical serial interface, with Python, C#
and JavaScript bindings. Open **`MCFluke.sln` in Visual Studio 2026** to work
with all components in one solution, as separate projects.

**Windows is the only supported build and runtime platform.** Linux/macOS
implementations, portability layers and artifacts are not included.
The native core uses the Windows serial, timing and C-runtime APIs directly.

The normal Windows workflow is **Build Solution, choose a startup project,
F5**. No shell build script or manually entered build command is
required. Visual Studio uses its standard build engine internally.

## Open, build and run

1. Open `MCFluke.sln` in Visual Studio 2026.
2. Select **Debug | x64** or **Release | x64**, then **Build Solution**.
3. Set **MCFluke.Exerciser** as the startup project. Choose a launch profile
   and press **F5**.

The C# profiles are **Fluke 187 - COM3**, **Simulator**, **Offline self-tests**
and **COM3 - 5 read-only samples**. The first is interactive. The offline
profiles never open a hardware port.

To run another language, set **MCFluke.C.Example**, **MCFluke.Python**, or
**MCFluke.JavaScript** as the startup project and press F5. The native
dependencies build first, and native-library/helper paths are supplied
automatically. Examples print identification and the primary display every
**two seconds**, without pressing buttons or resetting settings.

For the complete offline suite, set **MCFluke.OfflineChecks** as the startup
project and use **Start Without Debugging**. It runs already-built native,
Python, JavaScript and C# checks, including example cadence checks.

## Solution projects

| Project | Role |
|---|---|
| MCFluke.Native | C11 shared library (`mcfluke.dll`) |
| MCFluke.C.Example | Read-only C console program |
| MCFluke.Bridge | Persistent native helper for JavaScript |
| MCFluke.NativeTests | Native protocol/transcript test console |
| MCFluke (Managed folder) | .NET 10 binding library (`MCFluke.Managed.dll`) |
| MCFluke.Exerciser | Full C# console exerciser |
| MCFluke.Python | Python package, example and unit tests |
| MCFluke.JavaScript | Node.js package, example and tests |
| MCFluke.OfflineChecks | IDE-launchable complete offline check runner |

The `.vcxproj` files are Visual Studio's native project format, but the
compiler is explicitly set to **C**, with **C11** enabled. No C++ rewrite or
wrapper is involved. All bindings share this same native implementation;
there is no sigrok/PulseView dependency.

Both C# projects target **`net10.0-windows`**. Their native project dependency
is mandatory, not an optional or managed-only build path.

## Visual Studio requirements

The included `.vsconfig` lists the expected workloads:

- Desktop development with C++ (MSVC v145 and a Windows SDK).
- .NET desktop development (.NET SDK 10).
- Python development, with a **64-bit Python 3.10+** environment.
- Node.js development, with Node.js 22+.

If Visual Studio reports a missing workload, use the offered Visual Studio
Installer integration. The repository does not silently skip required
language tools. Choose your installed 64-bit interpreter in the Python
Environments UI; no user's interpreter or Node installation path is embedded.

## Hardware and safety

Use an electrically isolated optical adapter compatible with the Fluke 187.
A generic infrared device is not automatically compatible. The development
setup successfully read a real Fluke 187 through a CP210x-based optical head
on **COM3, 9600 baud, 8N1**, without flow control.

For communication checks, leave the measurement leads disconnected. Keep
the meter awake and align the optical transmitter/receiver with its IR
windows. Manual alignment can be intermittent. Communication does not
change the instrument's electrical ratings or safety requirements.

Only one process can own COM3 at a time. Stop another running example,
sigrok, or serial terminal before launching a different project.

## Full exerciser and clock synchronization

The C# menu exposes identification, single/repeated readings, remote buttons,
default setup, instrument reset, timeouts, parsers, version and status data.
Reading timestamps are **PC local time**, in `HH:mm:ss`; the exact meter
display string is preserved and numeric values are not SI-scaled.

Factory reset requires typed **RESET**. Calibration entry separately requires
**CALIBRATION**. Neither is performed automatically.

Menu **13** synchronizes the Fluke clock after typed **SYNC**. It measures
turnaround, targets the next feasible **0.1-second boundary**, verifies
clock readback and corrects again only if needed. Normal execution takes
seconds, not minutes. Other settings are preserved by read-modify-write.
The extended `QS` / `PS` path is experimental; see
[clock synchronization](docs/clock-sync.md) for evidence and accuracy limits.

## Outputs and deployment

Native Windows outputs are separated by architecture and configuration:

```text
out\vs\bin\x64\Debug\
out\vs\bin\x64\Release\
```

The C# project dependency copies the matching native DLL beside the managed
application automatically. Python/JavaScript projects provide exact native
paths in their debugger environments. Native intermediates are also
project/configuration-separated under `out\vs\obj`.

Discovery uses the Visual Studio output directories, then supported
Windows deployment locations; it does not search retired build directories.

Standalone/deployment overrides remain available: `MCFLUKE_LIBRARY` is an
absolute shared-library path; `MCFLUKE_HOST` is an absolute helper path.
They are optional in the solution workflow. Explicit overrides are
authoritative: failures do not select a different build or simulated meter.

NuGet/Python/npm packages contain the Windows bindings, not native
artifacts. Supply the native library/helper separately when deploying.
Existing public APIs and native ABI 1 are retained.

## Windows checks and maintenance

CI uses the same native/C# solution projects on a Visual Studio 2026 Windows
runner. `eng\MCFluke.Build.slnf` is a CI filter of the
same solution, not a second solution; it excludes IDE-only language project
types while their Python/Node runtimes run the complete checks.

`tools\build.py` and `tools\run_checks.py` are automation entry points, not
steps required from a Visual Studio user. They reject non-Windows execution
before invoking build tools or creating output directories. All native build
outputs use the Visual Studio paths above. Missing build products fail
explicitly rather than using a successful simulation fallback.

The maintainer-only `tools\verify_vs_build.py` checks configuration switching,
incremental builds and native-DLL copy recovery in a disposable sibling copy.
It keeps source inputs read-only and avoids `%TEMP%`, which MSVC excludes
from reliable file tracking. It also checks native-library discovery with
isolated current-output, adjacent-deployment and explicit-override fixtures,
and verifies that retired build outputs are not searched.

See [protocol coverage](docs/protocol.md), the binding READMEs and
[bridge contract](docs/bridge.md) for API details and limitations.

Initial development version: **0.1.0**, native ABI **1**. No redistribution
license has been selected yet.

Repository: <https://github.com/MiguelCaldasMSOrg/MCFluke>

Fluke is a trademark of its respective owner. This independent project is
not affiliated with or endorsed by Fluke.
