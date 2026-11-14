# Native JavaScript bridge

`mcfluke-host` is a small C executable linked to the same native library used by
C, Python and C#. The JavaScript binding talks to this persistent child process
using stdin/stdout, without native Node addons, npm runtime dependencies or a
second implementation of the Fluke protocol.

Start it as `mcfluke-host --port COM3 --timeout 1500`. Use `sim://187` for offline
tests. It verifies and opens the device, then emits one JSON line:

```json
{"type":"ready","ok":true}
```

An open failure emits `{"type":"ready","ok":false,"error":{...}}` and exits with
failure. Hardware diagnostics never masquerade as a successful simulated open.

## Requests and replies

Each request is one UTF-8 line containing:

```text
positive-request-id<TAB>verb<TAB>argument<LF>
```

The final tab is required even with an empty argument. IDs must fit JavaScript's
safe integer range. Requests must fit in 1023 bytes including LF. Arguments may
not contain tab, CR or LF. The bridge processes requests sequentially.

Replies are one JSON object per line, followed by LF:

```json
{"id":1,"ok":true,"result":null}
{"id":2,"ok":false,"error":{"status":8,"osCode":0,"message":"Explicit confirmation is required."}}
```

| Verb | Argument | Result |
|---|---|---|
| `id` | empty | `{model,firmware,serialNumber}` |
| `display` | empty | `{display,unit,state,value}` |
| `clock` | empty | raw uint32 decisecond counter |
| `set-clock` | integer deciseconds since midnight, 0..863999 | raw uint32 counter written |
| `button` | `code confirmation`, e.g. `17 0` | null |
| `defaults` | empty | null |
| `reset` | confirmation integer | null |
| `get-timeout` | empty | integer milliseconds |
| `set-timeout` | integer milliseconds | null |
| `version` | empty | `{version,abi}` |
| `parse-id` | ID payload, without ACK/framing | identity object |
| `parse-display` | full `QM,<display>` line, without ACK/framing | reading object |
| `status-message` | status integer | diagnostic string |
| `close` | empty | null, then normal process exit |

Reading states are `0` numeric, `1` out of range, `2` other text. Non-numeric
readings have JSON `value: null`, never a numeric zero stand-in. Reset and
calibration confirmations must be exactly `1`; 187-only restrictions still
apply. Structured failures preserve native status and OS diagnostics.

Clock writes read fresh settings and replace only the clock field. The
compound QS/PS operation shares one native timeout and is not retried on an
ambiguous acknowledgement. See [clock synchronization](clock-sync.md).

Stdout is reserved for protocol JSON; consumers must not parse terminal
decoration or diagnostics as replies. EOF closes the device. A malformed
request without a usable ID is returned with ID zero; clients should consider
that a bridge protocol failure.
