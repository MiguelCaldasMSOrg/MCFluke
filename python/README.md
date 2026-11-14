# Python binding

Python 3.10+; no runtime or test dependencies beyond the standard library.
The binding calls the **same native C core** through `ctypes.CDLL` (cdecl).
It does not implement a serial protocol. Importing `mcfluke` does not load native code.

## Visual Studio 2026

Open the root MCFluke solution in Visual Studio 2026 with Python development and
Desktop development with C++ installed. Select Debug or Release, build the
solution normally, set **MCFluke.Python** as the startup project, and press F5.
No separate native-build command, package installation or environment editing
is needed. Select an installed/global **64-bit Python** environment in Visual
Studio; the project does not pin an interpreter path or create a virtualenv.

`MCFluke.Python.pyproj` starts `examples\read_display.py` with `--port COM3`,
working directory `python`, and source search path `src`. The Debug/Release
language configurations use Any CPU, mapped from the solution's Debug|x64 and
Release|x64 configurations; the native project reference builds
`MCFluke.Native` for x64. Required imports are `..\eng\MCFluke.Paths.props` and
the installed VS18 `Python Tools\Microsoft.PythonTools.targets`, not optional
fallback imports. The shared props select `out\vs\bin\x64\$(Configuration)\`.
The project `Environment` sets `MCFLUKE_LIBRARY` to that configuration's exact
`mcfluke.dll` and `PYTHONPATH` to the project's `src` directory.
Language build outputs are isolated in `out\vs\bin\MCFluke.Python\$(Configuration)\`
and intermediates in `out\vs\obj\MCFluke.Python\$(Configuration)\`.
The base intermediate path is set before `Microsoft.Common.props`, so project
extension/cache files also stay under `out\vs`, not the source directory.
These paths do not change the exact native-library launch override.
The installed Python Tools select the interpreter; their **unittest** support
is configured for Test Explorer discovery in `tests`, pattern `test*.py`.
Build the solution before running native simulator tests in Test Explorer.

F5 uses the real COM3 port by default. To run without hardware, change the
project's Debug script arguments to `--port sim://187 --count 3`.
Both modes only read identity and the primary display.

## Optional direct example

After building the solution in Visual Studio, from this `python` directory:

```powershell
$env:PYTHONPATH = "$PWD\src"
python examples\read_display.py --port sim://187 --count 3
```

The same example is also available as `python -m mcfluke`. Both entry points
flush every output line immediately, including when stdout is piped.

For a real connected meter, explicitly select its port, for example
`python -m mcfluke --port COM3`. The default is COM3.
The example gets identity once, then prints the unmodified primary display,
unit, state and value **every two seconds**. It sends no button, default-setup,
reset or clock commands. `--count N` bounds readings (zero means identity only);
`--interval SECONDS` is useful for tests. `--timeout MS` defaults to 1500.
Ctrl-C closes the connection and exits 130; native/load failures print a useful
diagnostic and exit 1. No automatic reconnect or simulator fallback is performed.

If packaging is wanted, `pyproject.toml` describes a standard setuptools source
distribution. Setuptools is a build-only requirement, not an installed runtime
dependency; the commands here do not install or download anything.

## Native discovery

`library_path=` explicitly selects an absolute library file. Otherwise
`MCFLUKE_LIBRARY`, when set, must be an **absolute file path** and is authoritative
(a bad override never falls back). Without an override, discovery checks
ancestor `out\vs\bin\x64\Debug` and `Release` directories first, preferring Debug
when both exist at the same root. Then it checks adjacent and ancestor legacy
`build\bin` directories, Python's installation `lib`/`bin`, and the platform's
installed-library search. The IDE's explicit per-configuration override takes
precedence over that default search. Names are `mcfluke.dll`, `libmcfluke.so`
(also `.so.1`), or `libmcfluke.dylib`. Architecture/dependencies must match Python.
Missing libraries produce `DllNotFoundError` with remediation. ABI 1 is required.

## API

```python
from mcfluke import MCFluke, Button, ReadingState, parse_display, get_version

with MCFluke("sim://187", timeout=1500) as meter:
    identity = meter.get_identity()  # model, firmware, serial_number
    reading = meter.read_display()  # display, unit, state, value
    if reading.state == ReadingState.VALUE:
        print(reading.value, reading.unit)  # displayed units; no SI conversion
    else:
        print(reading.display)  # value is None, NOT a fake zero measurement
```

`Identity`, `Reading`, and `Version` are frozen dataclasses. Initial simulator
identity is `FLUKE 187` / `SIM-1.0` / `SIMULATED`, display `+1.234 V DC`.
Only the exact port `sim://187` selects this deterministic test double.

Device methods:

| Method | Meaning |
|---|---|
| `get_identity()` / `read_display()` | Read identity / primary display |
| `read_clock_counter()` | Raw unsigned 32-bit clock counter, in deciseconds |
| `set_clock(time_of_day_ticks)` | Set integer ticks since midnight; return the actual written raw counter |
| `get_timeout()` / `set_timeout(ms)` | Read/change native timeout (50..60000 ms) |
| `press_button(Button.RANGE)` | Native SF button command |
| `press_button(Button.CALIBRATION, confirm_calibration=True)` | Explicitly enter calibration workflow; does not calibrate |
| `default_setup()` | Restore default setup |
| `reset_instrument(confirmation=True)` | Erase user settings and reset clock; not calibration constants |
| `close()` / `closed` | Idempotent disposal / disposal state |

Destructive actions require **explicit boolean** confirmations, defaulting to
false; native confirmation guards remain authoritative. `Button.LOGGING` (23)
and `Button.SAVE` (30) are 189-only and unsupported on the 187. Do not use
control methods casually on real instruments.

Module functions `parse_identity(payload)`, `parse_display(full_qm_line)`,
`get_version()` (returns `Version(version, abi)`), and `status_message(status)`
also use the native ABI. Each accepts optional `library_path=`.
Parsers accept unframed responses, not ACK framing. Display state is
`VALUE=0`, `OUT_OF_RANGE=1`, or `TEXT=2`; unknown textual displays are preserved.
For example, `parse_identity("FLUKE 187,SIM-1.0,SIMULATED")` parses an ID payload;
`parse_display("QM,-12.5 mV DC")` retains the displayed `mV DC` units.

`MCFlukeError` carries `status`, `os_code`, and `operation`. Specific subclasses
include `DeviceTimeoutError` (also `TimeoutError`), `DeviceIOError`,
`ConfirmationRequiredError`, `UnsupportedOperationError`, `WrongModelError`,
`InvalidArgumentError`, `CommandRejectedError` and `ProtocolError`.
Calls after close raise `DeviceClosedError`.

All device calls, including close, are serialized by a per-instance reentrant
lock. Separate devices can operate independently. The context manager is the
preferred deterministic lifetime; a non-self-retaining finalizer is a fallback.

## Experimental clock support

Clock access uses the community-documented extended 18x **QS/PS** protocol.
This feature is **experimental, not manufacturer-certified**. It requires the
clock-capable native library (additive exports within ABI 1).

`read_clock_counter()` returns an integer in `0..4294967295`: a running raw
unsigned 32-bit **decisecond counter**, not Unix/UTC time or a date/epoch.
`CLOCK_TICKS_PER_SECOND` is 10 and `CLOCK_TICKS_PER_DAY` is 864000.
`set_clock(time_of_day_ticks)` accepts only integer `0..863999` ticks since
midnight. Booleans, strings, fractional seconds/ticks and out-of-range values
are rejected, without rounding or wrapping. No automatic timezone conversion
or synchronization occurs.

The setter delegates to the native core: it reads the current 29-byte settings
block using a **fresh QS**, changes only the four clock bytes, and writes via PS.
The entire QS-read/PS-write operation shares **one native timeout/deadline**;
the timeout is not renewed for each transaction. All non-clock settings are
preserved. The core chooses the closest representable
day to the existing raw counter, including natural midnight rollover. The
returned integer is the **exact raw counter written after acknowledgement**,
which can differ from the input by whole days; it is not a later readback.
Readback can advance because the clock continues to run, and the raw uint32
counter eventually wraps. Both operations share the existing per-device lock,
disposal guards and structured error handling. Clock operations never run
implicitly in the examples.

## Offline tests and maintainer checks

Visual Studio Test Explorer uses the configured stdlib unittest discovery.
For portable command-line test runs from `python`, with `PYTHONPATH` set as above:

```powershell
python -m unittest discover -s tests -v
```

These tests never open a real serial port: integration tests use `sim://187`
and explicitly fail with Visual Studio build instructions if the native library is
missing. Binding-only tests (ABI declarations, error conversion, concurrency,
finalization, disposal, clock validation/errors and read-only example behavior)
need no native build. They also cover VS output resolution and override precedence:

```powershell
python -m unittest discover -s tests -p test_binding.py -v
```

Root-build integration uses source directory `python\src\mcfluke`, test
directory `python\tests`, and example `python\examples\read_display.py`:

```powershell
$env:PYTHONPATH = "$PWD\python\src"
python -m unittest discover -s python\tests -v
python python\examples\read_display.py --port sim://187 --count 3
```
