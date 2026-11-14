# Fluke 187 remote interface

MCFluke implements the five commands in Fluke's **189/187/89-IV/87-IV Remote
Interface Specification**, last updated 2000-11-27:

<https://www.pewa.de/DATENBLATT/DBL_FL_FL187-9-89IV_BEFEHLSSATZ_ENGLISCH.PDF>

The implementation is original C code based on the documented interface. The
Fluke document and sigrok source code are not bundled in this repository.
Fluke's technical note describes only some serial capabilities. Extended
clock access uses the independently evidenced 18x `QS` / `PS` commands
described in [clock synchronization](clock-sync.md), not guessed commands.

## Wire framing and supported operations

9600 baud, eight data bits, no parity, one stop bit, no hardware or software
handshake. Commands terminate with CR, not LF. Responses begin with `0<CR>` for
success or `1<CR>` for rejection. Successful queries have another CR-terminated
ASCII line for the original text commands. The extended QS response is
length-framed binary data. All phases of an operation share a bounded timeout.

| Command | C entry point | Response / effect |
|---|---|---|
| `ID` | `mcfluke_get_identity` | Model, firmware version, serial number |
| `QM` | `mcfluke_read_display` | Current **primary** display, including displayed units |
| `SF nn` | `mcfluke_press_button` | A remote button press; meter can reject it in its present mode |
| `DS` | `mcfluke_default_setup` | Cancel / return to power-up state, **not** factory reset |
| `RI` | `mcfluke_reset_instrument` | Reset user settings and clock; explicit confirmation required |
| `QS` (extended) | `mcfluke_get_clock` | Running uint32 decisecond clock counter |
| `QS` then `PS` (extended) | `mcfluke_set_clock` | Fresh settings read; replace only four clock bytes; acknowledge |

`mcfluke_open` verifies that the attached instrument identifies as `FLUKE 187`
before exposing a usable connection. There are no automatic retries of
state-changing commands. A timeout does **not** prove that the command failed
to execute; blindly repeating a toggle can undo the first press.

## Remote buttons

| Code | Button | Support |
|---|---|---|
| 10 | Blue | Supported |
| 11 | Hold | Supported |
| 12 | Min/Max | Supported |
| 13 | Relative | Supported |
| 14 | Up | Supported |
| 15 | Shift | Supported |
| 16 | Hz | Supported |
| 17 | Range | Supported |
| 18 | Down | Supported |
| 19 | Backlight | Supported |
| 20 | Calibration | Explicit, separate confirmation required |
| 21 | Auto Hold | Supported |
| 22 | Fast Min/Max | Supported |
| 23 | Logging | Fluke 189-only; rejected locally on the 187 |
| 24-26 | Unused | Invalid argument; never transmitted |
| 27 | Cancel | Supported |
| 28 | Wakeup | Supported; an optically unresponsive/off meter may still need physical wakeup |
| 29 | Setup | Supported |
| 30 | Save | Fluke 189-only; rejected locally on the 187 |

Calibration entry only sends `SF 20`; this library does not carry out or verify
calibration. Consult the official calibration manual and appropriate standards.
Factory reset does not erase calibration constants.

## Reading semantics and limitations

The `display` string preserves the primary display payload exactly, without
the `QM,` prefix or transport framing. A numeric `value` remains in its displayed
units: `+1.881 mV AC` is **1.881**, not 0.001881. The `unit` is the meter's text,
not a guessed physical quantity. Do not treat range overload as a numeric zero:
the C API's `state` differentiates numeric, out-of-range and other text states;
bindings expose nullable values where appropriate.

Numbers are converted by the C runtime using an explicit C numeric locale:
`_strtod_l` on Windows and `strtod_l` on POSIX. There is no handwritten
decimal arithmetic and the process's numeric locale is not changed. The
decimal wire grammar is validated separately; malformed numbers, overflow,
underflow to zero, non-ASCII framing contamination and oversized fields are
rejected. Finite representable subnormal values remain valid. Unrecognized
textual displays are preserved rather than turned into fabricated readings.

The C# exerciser prefixes readings with `HH:mm:ss` from the PC's local clock
when the reading is printed, not from the Fluke. `QM` contains no timestamp.
Numeric convenience values use concise formatting; the original `display`
text retains the meter's exact digits and trailing zeros.

The original published five-command set does **not** provide a secondary-display query,
complete LCD annunciators, timestamps from the meter, direct rotary-switch
position changes, or a full structured setup/clock/calibration API. Remote
button presses can navigate some modes, but cannot physically move the rotary
switch. The separate community clock extension does not expose a general
settings editor or calibration API. Do not infer more precision or update rate
than the instrument provides.

## Ownership and errors

`include/mcfluke.h` is the ABI contract. A successful open owns one connection;
close it exactly once. `mcfluke_close(NULL)` is harmless. Do not reuse a pointer
after close or overlap calls on one native device, including close. Managed
bindings serialize per-instance access and guard disposed instances.

Each fallible native operation returns a status and can fill an optional
`mcfluke_error` containing status, operating-system code and diagnostic text.
Failed open returns a null device. Identity/reading outputs are cleared on
failure; only consume them when the status is success. ABI version is checked
by the bindings before use.

## Simulator

The exact port name `sim://187` explicitly selects a deterministic test double.
It is **never** selected because a real port failed. It identifies with firmware
`SIM-1.0`, serial `SIMULATED`, and initially reports `+1.234 V DC`. Blue toggles
AC/DC; Range toggles V/mV. Defaults, reset and Cancel restore its baseline.
Other supported buttons acknowledge without emulating the complete meter UI.
Its QS/PS clock runs at decisecond resolution; RI resets that simulated clock.

The simulator tests protocol/binding integration, not electrical behavior,
measurement accuracy, calibration, every mode-dependent rejection, or safety.
