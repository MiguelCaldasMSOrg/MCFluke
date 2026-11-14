# Fluke 187 clock synchronization

Select **13** in the C# exerciser and type **SYNC**. Keep the optical head fixed,
leave the meter awake, and do not operate its buttons or change settings during
the operation. Normal execution spans three minute boundaries; a fourth write
is used if the measured residual offset still excludes zero.

The target is the **PC's local time**, including its time-zone offset at the
target instant. No date is programmed. The ordinary reading timestamps printed
by the exerciser remain PC timestamps, not clock readings from the meter.

## What the protocol supports

The front-panel clock editor exposes hours and minutes, but inspectable
18x-specific community implementations expose a running **uint32 decisecond
counter** through binary `QS` / `PS` settings commands. This is separate from
the original five-command Fluke technical note.

Protocol evidence:

- [f189ctrl clock getter/setter](https://github.com/cytrinox/f189ctrl/blob/8aa4a83ab11edf12aef485e4c3bb99f24ca5d10f/src/device.rs#L86-L122)
- [Command framing](https://github.com/cytrinox/f189ctrl/blob/8aa4a83ab11edf12aef485e4c3bb99f24ca5d10f/src/proto/codec.rs#L200-L231)
- [Binary settings layout](https://github.com/cytrinox/f189ctrl/blob/8aa4a83ab11edf12aef485e4c3bb99f24ca5d10f/src/proto/response.rs#L245-L290)
- [Independent QS clock-field description](https://github.com/C-X1/LibFluke/blob/41c24d3762d2b7f8fa4565a60b832ae3516ffebb/include/Fluke189.hpp#L98-L168)

This extended path is **experimental**, not manufacturer-certified. The cited
implementation declares 187 support, but does not establish exact firmware
latch timing. Clock writes on a physical 187 are not performed by the build,
examples, automated suite, or this feature's development checks.

### Wire contract

`QS<CR>` returns `0<CR>QS,` followed by **29 binary bytes**, with no assumed
trailing CR. Body bytes **12..15** hold the little-endian clock counter. The
body may itself contain NUL and CR.

`PS<space><29 binary bytes><CR>` returns an acknowledgement. Every clock setter
first reads a **fresh** settings block and changes **only those four clock
bytes**. All other bytes, including unknown fields, are copied unchanged.
There is no reset, calibration command, factory-default operation, or
front-panel menu navigation.

`mcfluke_set_clock` accepts 0..863999 deciseconds since midnight. It chooses the
nearest representable counter day, so a change from just before midnight to
00:00 advances naturally rather than resetting an elapsed-day counter. It
returns the exact raw counter sent after acknowledgement. A failed operation
is never automatically retried because the write may already have executed.

The entire read-modify-write operation shares one native timeout. Its runtime
includes the preliminary **QS read**, not just the final PS transmission.

## Timing procedure

1. Collect 12 clock reads and turnaround measurements. Verify that the returned
   field advances consistently with a decisecond clock **before writing**.
2. Estimate the first setter launch lead from measured QS turnaround plus a
   wire-length-aware PS estimate. At 9600 8N1, QS has 3 outgoing and 34 incoming
   bytes; PS has 33 outgoing and 2 acknowledgement bytes.
3. Choose a future PC minute with preparation margin. Convert that absolute
   instant to local time and use a **monotonic** deadline for scheduling.
   The last at-most-30 ms uses a bounded spin wait rather than relying on a
   coarse sleep; the system-wide timer resolution is not changed.
4. Start the entire read-modify-write early enough to compensate its expected
   application latency. A missed launch deadline over 50 ms is skipped, not
   silently used for a late setting.
5. After acknowledgement, allow one counter tick before collecting 24 clock
   readbacks. Derive a consistent
   meter-minus-PC offset interval, accounting for 100 ms counter quantization
   and each request/response timing bracket.
6. If zero lies outside that interval, adjust the next minute's launch lead by
   subtracting its midpoint. This empirically compensates the whole operation,
   including firmware processing and retained clock-divider phase, rather than
   merely assuming half a round trip.
7. Perform at least three writes for repeat confirmation. Allow a fourth when
   needed; otherwise report nonconvergence instead of claiming synchronization.

A positive offset means the meter is ahead of the PC. A negative launch lead
is allowed: retaining the meter's internal divider phase can require starting
slightly after a PC minute transition.

## Accuracy and failure reporting

The displayed offset envelope is an **observed interval**, not a guarantee or
calibration certificate. It assumes QS samples its counter during the measured
transaction and that the clock behaves as a decisecond counter. A 2 ms
allowance covers clock sampling granularity and short-window drift.

The exact instant at which firmware latches PS, acknowledgement semantics and
divider reset behavior are not specified by Fluke's original technical note.
Readback feedback addresses these uncertainties empirically, but does not
turn Windows/Linux/macOS or USB serial into a real-time system.

Significant PC clock/time-zone changes, frozen or malformed counter data,
inconsistent readbacks, lost acknowledgements and excessive scheduling
delays stop the operation explicitly. Scheduling is bounded to five minutes.
Ctrl+C stops future writes after any in-flight native operation finishes.
Already acknowledged clock changes are **not undone** on cancellation or
failure.

## Testing and compatibility

The virtual-clock tests exercise whole minute waits instantly, including
latency compensation, divider phase, midnight, non-UTC zones, clock steps,
late deadlines, nonconvergence, counter rollover and cancellation. Native
tests verify binary framing, fresh read-modify-write, non-clock byte
preservation and uncertain-delivery handling. POSIX pseudo-terminal tests use
the actual serial backend with embedded NUL/CR settings bytes.

The existing ABI and APIs remain unchanged. The two native clock functions
and their language bindings are additive; rebuild the native library together
with updated bindings. An older native build does not provide the new exports.
