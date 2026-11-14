# MCFluke

Plain-C access to the **Fluke 187** optical serial interface, with Python, C#
and JavaScript bindings and runnable programs in all four languages.

All bindings use the same C implementation. There is no dependency on sigrok,
PulseView, Python serial packages, npm native addons, or C++.

| Component | Purpose |
|---|---|
| `include/`, `src/` | C11 shared library, Windows and POSIX serial backends |
| `examples/c/` | Read-only ID and primary-display program |
| `python/` | Python `ctypes` binding, example and tests |
| `csharp/` | .NET 10 binding and comprehensive console exerciser |
| `javascript/` | Node.js binding through the persistent native C bridge |
| `tests/` | Protocol transcripts, bridge, serial-backend and example-cadence checks |
| `docs/` | Command coverage, limitations and bridge contract |

Repository: <https://github.com/MiguelCaldasMSOrg/MCFluke>

## Hardware

Use an electrically isolated optical adapter that is compatible with the
Fluke 187. A generic infrared device is not automatically compatible. The
initial development setup successfully identified a real Fluke 187 with a
CP210x-based optical head on **COM3 at 9600 8N1**, without flow control.

Keep the optical transmitter/receiver aligned with the meter's IR windows.
Test communications with the measurement leads disconnected. Never bypass the
meter's isolation, modify the meter's safety features, or assume a software
connection changes its electrical ratings. Communication tests do not require
a live circuit.

Only one process can own the Windows COM port at a time. Stop sigrok or other
serial tools before starting an MCFluke program. The meter must remain awake.

## Build everything

Prerequisites:

- A C11 compiler and CMake 3.20 or newer. On Windows, use MSVC or MinGW-w64.
- Python 3.10 or newer.
- .NET SDK 10.
- Node.js 22 or newer.

From the repository root on Windows:

```powershell
python tools\build.py
```

The script builds the native library/programs and C# projects, then runs the
offline checks in every language. **It never opens COM3 or sends commands to
real hardware.** Tests explicitly select `sim://187`.

An existing CMake cache is reused. For a fresh build with a specific generator:

```powershell
python tools\build.py --generator "MinGW Makefiles"
```

Use `--configuration Debug` for debug builds or `--skip-tests` to build only.
Do not change generators inside an existing build directory.

On Linux/macOS:

```sh
python tools/build.py
```

No third-party Python or JavaScript runtime packages are required. The Node
binding starts the project's native helper, not a shell command.

### C-only build

```powershell
cmake -S . -B build -G "MinGW Makefiles" -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
ctest --test-dir build -C Release --output-on-failure
```

For MSVC omit `-G "MinGW Makefiles"` on a fresh build. Native tests require
Python; `-DBUILD_TESTING=OFF` permits a C-only build without it. Native outputs
are in `build\bin`, including `mcfluke.dll`, `mcfluke-example.exe` and
`mcfluke-host.exe`. Linux/macOS use the normal `libmcfluke.so` /
`libmcfluke.dylib` naming and executables without `.exe`.

The C ABI is declared in [include/mcfluke.h](include/mcfluke.h). Native callers
include that header and link the generated import/shared library. CMake
installation is supported with `cmake --install build --prefix <destination>`.

## Run against the meter

The C, Python and JavaScript examples print identification once and then print
the current primary display every **two seconds**. They never press buttons
or reset settings. `--count 3` makes these examples stop after three readings;
omit it to run until Ctrl+C.

```powershell
.\build\bin\mcfluke-example.exe --port COM3 --count 3
python python\examples\read_display.py --port COM3 --count 3
node javascript\examples\read-display.js --port COM3 --count 3
```

The Python checkout example locates its source package itself; it does not
require installation or a `PYTHONPATH` setting. Its standalone launch is checked
with Python's isolated mode, which ignores `PYTHONPATH`.

Replace `COM3` with the actual port. Linux usually uses `/dev/ttyUSB0`; macOS
usually uses a `/dev/cu.*` device. Change the physical selector yourself when
the desired function requires moving the rotary switch.

### Full C# exerciser

```powershell
dotnet run --project csharp\MCFluke.Exerciser -- --port COM3
```

The console exerciser provides identification, displayed readings, remote
buttons, default setup, factory reset, timeout configuration, parser inspection
and library information. It can exercise the entire exposed library, not just
the read-only example path. Use `--help` for its command-line modes.

Factory reset requires typed confirmation. Calibration entry has a separate
confirmation and warning; it is not an automatic calibration procedure.
Neither is performed automatically on real hardware.

Menu **13** synchronizes the Fluke clock to the PC's local time after typed
**SYNC** confirmation. It measures turnaround, targets the next feasible
0.1-second boundary, reads the clock back, and corrects again only if needed.
It normally finishes in seconds, without minute waits or mandatory repeated
writes. Other settings are preserved. This uses experimental
18x `QS` / `PS` commands; see [clock synchronization](docs/clock-sync.md) for
protocol evidence, timing assumptions and accuracy limits.

Run its complete offline self-test explicitly:

```powershell
dotnet run --project csharp\MCFluke.Exerciser -- --self-test
```

Language-specific usage is documented in each binding's README.

## Build library packages

The shared native library is built by CMake. Package the managed/interpreted
bindings separately when needed:

```powershell
dotnet pack csharp\MCFluke --configuration Release --output out\packages
python -m pip wheel --no-deps --wheel-dir out\packages .\python
npm pack .\javascript --pack-destination out\packages
```

These packages do not bundle a platform-specific native library/helper. Deploy
the native outputs alongside your application or set the discovery overrides
below. None of these commands publishes to NuGet, PyPI or npm.

## Native-library discovery

Build before running the examples. The bindings support repository-local native
outputs and explicit deployment overrides:

- `MCFLUKE_LIBRARY`: absolute path to the native shared library for Python/C#.
- `MCFLUKE_HOST`: absolute path to the native helper for JavaScript; keep the
  corresponding shared library beside it on Windows.

These are process environment variables, not changes to system-wide settings.
Wrong native architecture, missing files and serial failures are reported
explicitly. Real-connection failures never fall back to simulated readings.

## Coverage and limitations

The documented interface provides `ID`, `QM`, `SF`, `DS` and `RI`. MCFluke
preserves the displayed text, numerical value in displayed units, and
out-of-range/text states. It does not silently convert mV to V or an overload
to a zero-valued measurement.

Clock access additionally uses community-documented binary `QS` / `PS`.
The front-panel editor is minute-resolution, but this counter uses deciseconds.
These commands are not part of Fluke's original five-command technical note.

`QM` reports the **primary** display only. The published interface does not
give a full LCD/secondary-display query or remote rotary-switch control.
Logging and Save are Fluke 189-only and are rejected locally for this 187
library. See [the protocol coverage and safety notes](docs/protocol.md).

## Tests and CI

The public CI workflow builds on Windows, Linux and macOS, with no hardware
access. It covers the shared protocol, state-changing command guards, native
bridge, language bindings and the real two-second default output cadence.
POSIX pseudo-terminal tests exercise fragmented replies and deadlines through
the serial backend; they are explicitly skipped on Windows.

The deterministic simulator is a protocol test double, not an electrical model
or a substitute for checking accuracy with calibrated equipment.

## Project status

Initial development version: **0.1.0**, native ABI **1**. APIs may evolve before
a stable release. No redistribution license has been selected yet.

Fluke is a trademark of its respective owner. This independent project is not
affiliated with or endorsed by Fluke.
