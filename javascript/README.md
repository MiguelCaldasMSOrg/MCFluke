# JavaScript binding

Node.js 22+ on Windows; **zero npm runtime/test dependencies**. This ES module
spawns one persistent native `mcfluke-host` process per connection, without a shell.
The host calls the shared C core; JavaScript does **not** implement a serial
protocol. Importing the module never launches a process.

## Visual Studio 2026

Open the root MCFluke solution in Visual Studio 2026 with Node.js development
and Desktop development with C++ installed. Select Debug or Release, build the
solution normally, set **MCFluke.JavaScript** as the startup project, and press
F5. No separate native-build command, npm installation or environment editing
is needed. Visual Studio selects the installed/global Node.js runtime; no
machine-specific `NodeExePath` is stored in the project.

`MCFluke.JavaScript.esproj` uses Visual Studio's modern JavaScript Project
System (JSPS), with `Microsoft.VisualStudio.JavaScript.Sdk` pinned to the
version shipped with Visual Studio 2026. Visual Studio resolves the SDK;
the retired Node.js Tools project targets are no longer used.
The Debug/Release language configurations use Any CPU, mapped from the
solution's Debug|x64 and Release|x64 configurations. The native project
reference builds `MCFluke.Bridge` for x64 and its native-core dependency.
The shared `..\eng\MCFluke.Paths.props` selects
`out\vs\bin\x64\$(Configuration)\`.

The checked-in `.vscode\launch.json` supplies Node debugger profiles, not a
browser. **Fluke 187 - COM3** starts `examples\read-display.js` with
`--port COM3` and working directory `javascript`. Every project build writes
the exact configuration's `MCFLUKE_HOST` into
`out\vs\obj\MCFluke.JavaScript\launch.env`, read by both debugger profiles.
The IDE's fast up-to-date shortcut is disabled for this project so switching
between existing Debug/Release outputs always refreshes that launch environment;
native project builds remain incremental.
Language build outputs are isolated in `out\vs\bin\MCFluke.JavaScript\$(Configuration)\`
and intermediates in `out\vs\obj\MCFluke.JavaScript\$(Configuration)\`.
The base intermediate path is set before the SDK props, keeping
project extension/cache files under `out\vs` rather than the source directory.
Automatic npm installation and build scripts are disabled: this package has no
dependencies and does not need transpilation or a bundler. The existing npm
scripts and public binding API are unchanged.

F5 uses the real COM3 port by default. For a bounded hardware-free run, select
the **Simulator** debugger profile (`--port sim://187 --count 3`).
Both modes only read identity and the primary display.
Tests continue to use Node's built-in `node:test` via the existing `npm test`
script; no Mocha/Jest adapter or dependencies are added.

## Optional direct read-only example

After building the solution in Visual Studio, from this `javascript` directory:

```powershell
node examples\read-display.js --port sim://187 --count 3
```

For a real connected meter explicitly use its port, for example `--port COM3`
(also the default). The example gets identity once and prints each primary
display, unit, state and value **every two seconds**. It sends no button,
default-setup, reset or clock commands. `--count N` bounds reads (zero means identity
only), `--interval SECONDS` speeds up tests, and `--timeout MS` defaults to 1500.
Ctrl-C disposes the connection and exits 130. Errors print diagnostics and exit 1.
There is no automatic reconnect or fallback to simulation.

## API

```javascript
import { MCFluke, ReadingState } from './src/index.js';

const meter = await MCFluke.open('sim://187', { timeout: 1500 });
try {
  const id = await meter.getIdentity(); // { model, firmware, serialNumber }
  const reading = await meter.readDisplay(); // { display, unit, state, value }
  if (reading.state === ReadingState.VALUE) console.log(reading.value, reading.unit);
  else console.log(reading.display); // value is null, never a fake zero measurement
} finally {
  await meter.close();
}
```

Only exact `sim://187` selects the native deterministic test double: initial
display `+1.234 V DC`, model `FLUKE 187`, firmware `SIM-1.0`, serial `SIMULATED`.
The real port is verified as a FLUKE 187 before the host announces readiness.

`hostPath` in `open` overrides discovery; otherwise `MCFLUKE_HOST` is used when
set. Either override must name an absolute executable file, and a bad override
never falls back. Default discovery checks ancestor
`out\vs\bin\x64\Debug` and `Release` directories first, preferring Debug when both
exist at the same root. It then checks package-adjacent and ancestor files
for `mcfluke-host.exe`; retired build directories are not searched.
Non-Windows execution is rejected before helper discovery.
The host needs its matching native library available. Missing
hosts raise `HostNotFoundError`. No global configuration or environment changes
are made by this module.

All methods are asynchronous, serialized per client and return promises:

| Method | Result / behavior |
|---|---|
| `getIdentity()` | Frozen `{model, firmware, serialNumber}` |
| `readDisplay()` | Frozen `{display, unit, state, value}` |
| `readClockCounter()` | Raw unsigned 32-bit clock counter in deciseconds |
| `setClock(timeOfDayTicks)` | Set integer ticks since midnight; return actual written raw counter |
| `getTimeout()` / `setTimeout(ms)` | Number / null; native timeout 50..60000 ms |
| `pressButton(Button.RANGE)` | Native SF button command; null |
| `pressButton(Button.CALIBRATION, {confirmCalibration:true})` | Explicitly enter calibration workflow, not perform calibration; null |
| `defaultSetup()` | Restore default setup; null |
| `resetInstrument({confirmation:true})` | Erase user settings and reset clock, not calibration constants; null |
| `parseIdentity(payload)` / `parseDisplay(fullQmLine)` | Same native parsers, frozen identity / reading |
| `getVersion()` | Frozen `{version, abi}`; ABI 1 |
| `statusMessage(status)` | Native status description string |
| `close()` | Idempotent promise for graceful shutdown |

Confirmations must be explicit booleans and default to false. The native guards
are authoritative. `Button.LOGGING` (23) and `Button.SAVE` (30) are 189-only,
unsupported for the 187. Control methods are not part of the read-only example.

States: `ReadingState.VALUE=0`, `OUT_OF_RANGE=1`, `TEXT=2`. Values use displayed
units without SI conversion. Nonnumeric states have `value:null`; original
display text and units are preserved. Parser inputs are unframed; request
arguments cannot contain CR, LF, TAB or NUL and are bounded in size.
Example parser inputs are `FLUKE 187,SIM-1.0,SIMULATED` and `QM,-12.5 mV DC`.

`MCFlukeError` carries `status`, `osCode`, `operation` and `stderr`.
Native-specific subclasses include `NativeTimeoutError`,
`ConfirmationRequiredError`, `UnsupportedOperationError`, `WrongModelError`,
`InvalidArgumentError`, `CommandRejectedError`, and `ProtocolError`.
`BridgeError` reports failed startup, unexpected process exit or a missing
response; transport failures terminate the host and reject pending/queued calls.
`diagnostics` retains the most recent 64 KiB of stderr text.
Native timeout errors do not poison a healthy connection; a bridge deadline does.

Calls accepted before `close()` drain in FIFO order; calls after it immediately
reject with `DeviceClosedError`. Queued calls after a transport failure reject
without sending. Deadlines are computed when requests actually start, using the
most recently successful native timeout (twice that timeout plus one second).
The startup budget uses the same formula. Clock writes retain a conservative
bridge watchdog of four times the native timeout plus one second. These are
transport watchdogs, not additional native transaction budgets: the entire
native QS-read/PS-write operation shares **one timeout/deadline**, without
renewing it between transactions.
`closed` becomes true as soon as
shutdown/failure starts. Explicit `try/finally` disposal is recommended;
`Symbol.asyncDispose` is supported where the runtime supports async disposal.

## Experimental clock support

Clock access uses the community-documented extended 18x **QS/PS** protocol.
This feature is **experimental, not manufacturer-certified** and requires a
clock-capable native host/core (additive exports within ABI 1).

`readClockCounter()` sends bridge verb `clock` with an empty argument and
returns a safe integer in `0..4294967295`. This is a running raw unsigned 32-bit
**decisecond counter**, not Unix/UTC time or a date/epoch.
`CLOCK_TICKS_PER_SECOND` is 10 and `CLOCK_TICKS_PER_DAY` is 864000.
`setClock(timeOfDayTicks)` accepts only an integer `0..863999` ticks since
midnight and sends `set-clock` with that decimal argument. Booleans, strings,
BigInts, fractions and out-of-range values are rejected locally; there is no
rounding, wrapping, timezone conversion or automatic synchronization.

The native core performs a **fresh QS** read of the current 29-byte settings
block, changes only the four clock bytes and writes via PS, preserving every
non-clock setting. Both transactions share one native operation deadline.
It chooses the closest representable day to the existing raw
counter, including natural midnight rollover. The setter returns the **exact raw
uint32 counter written after acknowledgement**, which can differ from the input
by whole days; it is not a later readback. Readback can advance as the clock
runs, and the raw counter eventually wraps. Both response types are validated as
safe integers in the full unsigned 32-bit range. Operations use the same
per-client queue, disposal guards and structured errors as existing methods.
The read-only examples never invoke clock operations.

## Offline tests and maintainer checks

After building the solution in Visual Studio, use its integrated terminal in
`javascript` (these are tests, not separate build steps):

```powershell
npm test
```

No `npm install` is necessary. Integration tests use only native `sim://187`
and **fail clearly**, rather than silently skipping, if native build artifacts
are missing. Transport tests use checked-in fake-child fixtures to cover split
UTF-8/JSON, malformed replies, startup/operation deadlines, stderr, exited
processes, serialization and shutdown:

```powershell
npm run test:unit
```

Resolver regressions need no native binaries:

```powershell
node --test test\resolver.test.js
```

They verify VS Debug/Release precedence, adjacent Windows deployment lookup,
rejection of non-Windows execution and retired build directories, and authoritative
explicit overrides without writing to native output directories or changing
the parent process environment. The fake child is strictly a transport fixture,
not a serial implementation.
