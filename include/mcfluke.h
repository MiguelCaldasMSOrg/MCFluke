#ifndef MCFLUKE_H
#define MCFLUKE_H

#include <stdint.h>

#if !defined(_WIN32)
#error MCFluke requires Windows.
#endif

#if defined(MCFLUKE_BUILD)
#define MCFLUKE_API __declspec(dllexport)
#else
#define MCFLUKE_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct mcfluke_device mcfluke_device;

typedef enum {
    MCFLUKE_OK = 0,
    MCFLUKE_INVALID_ARGUMENT = 1,
    MCFLUKE_IO_ERROR = 2,
    MCFLUKE_TIMEOUT = 3,
    MCFLUKE_PROTOCOL_ERROR = 4,
    MCFLUKE_COMMAND_REJECTED = 5,
    MCFLUKE_BUFFER_TOO_SMALL = 6,
    MCFLUKE_WRONG_MODEL = 7,
    MCFLUKE_CONFIRMATION_REQUIRED = 8,
    MCFLUKE_UNSUPPORTED = 9,
    MCFLUKE_OUT_OF_MEMORY = 10
} mcfluke_status;

typedef enum {
    MCFLUKE_BLUE = 10,
    MCFLUKE_HOLD = 11,
    MCFLUKE_MIN_MAX = 12,
    MCFLUKE_REL = 13,
    MCFLUKE_UP = 14,
    MCFLUKE_SHIFT = 15,
    MCFLUKE_HZ = 16,
    MCFLUKE_RANGE = 17,
    MCFLUKE_DOWN = 18,
    MCFLUKE_BACKLIGHT = 19,
    MCFLUKE_CALIBRATION = 20,
    MCFLUKE_AUTO_HOLD = 21,
    MCFLUKE_FAST_MIN_MAX = 22,
    MCFLUKE_LOGGING = 23,
    MCFLUKE_CANCEL = 27,
    MCFLUKE_WAKEUP = 28,
    MCFLUKE_SETUP = 29,
    MCFLUKE_SAVE = 30
} mcfluke_button;

typedef enum {
    MCFLUKE_READING_VALUE = 0,
    MCFLUKE_READING_OUT_OF_RANGE = 1,
    MCFLUKE_READING_TEXT = 2
} mcfluke_reading_state;

typedef struct {
    int32_t status;
    int32_t os_code;
    char message[256];
} mcfluke_error;

typedef struct {
    char model[32];
    char firmware[32];
    char serial_number[64];
} mcfluke_identity;

typedef struct {
    double value;
    int32_t state;
    char display[128];
    char unit[32];
} mcfluke_reading;

enum {
    MCFLUKE_CLOCK_TICKS_PER_SECOND = 10,
    MCFLUKE_CLOCK_TICKS_PER_DAY = 864000
};

MCFLUKE_API const char *mcfluke_version(void);
MCFLUKE_API uint32_t mcfluke_abi_version(void);
MCFLUKE_API const char *mcfluke_status_message(int32_t status);

/* Serial: 9600 8N1, no handshake. The exact port "sim://187" selects the
 * deterministic test double, never a fallback for a failed real connection.
 * Open verifies ID before permitting control. Timeout is 50..60000 ms.
 * The error output may be NULL. On failed open, *device is always NULL. */
MCFLUKE_API mcfluke_status mcfluke_open(
    const char *port, uint32_t timeout_ms, mcfluke_device **device,
    mcfluke_error *error);
MCFLUKE_API void mcfluke_close(mcfluke_device *device);

/* A device's calls, including close, must not overlap. Separate devices may
 * be used independently. Bindings serialize calls on each instance. */
MCFLUKE_API mcfluke_status mcfluke_get_identity(
    mcfluke_device *device, mcfluke_identity *identity, mcfluke_error *error);
MCFLUKE_API mcfluke_status mcfluke_read_display(
    mcfluke_device *device, mcfluke_reading *reading, mcfluke_error *error);
/* Extended 18x protocol: QS returns a running uint32 decisecond counter.
 * PS replaces settings, so set_clock first reads the current 29-byte settings
 * block and changes ONLY its four clock bytes. No other settings are altered.
 * time_of_day_ticks must be 0..863999. The nearest representable counter day
 * is retained, including a natural midnight rollover. written_counter may be
 * NULL; otherwise it receives the exact uint32 value sent after ACK success.
 * The setter's turnaround includes the preliminary QS transaction. */
MCFLUKE_API mcfluke_status mcfluke_get_clock(
    mcfluke_device *device, uint32_t *counter, mcfluke_error *error);
MCFLUKE_API mcfluke_status mcfluke_set_clock(
    mcfluke_device *device, uint32_t time_of_day_ticks, uint32_t *written_counter,
    mcfluke_error *error);
MCFLUKE_API mcfluke_status mcfluke_get_timeout(
    mcfluke_device *device, uint32_t *timeout_ms, mcfluke_error *error);
MCFLUKE_API mcfluke_status mcfluke_set_timeout(
    mcfluke_device *device, uint32_t timeout_ms, mcfluke_error *error);

/* SF 20 requires confirm_calibration == 1. This enters the meter's
 * calibration workflow; it does not perform or validate calibration.
 * SF 23 / SF 30 are 189-only and return UNSUPPORTED without transmission.
 * Other keys can be rejected by the meter in its current mode. */
MCFLUKE_API mcfluke_status mcfluke_press_button(
    mcfluke_device *device, int32_t button, int32_t confirm_calibration,
    mcfluke_error *error);
MCFLUKE_API mcfluke_status mcfluke_default_setup(
    mcfluke_device *device, mcfluke_error *error);
/* RI erases user settings and resets the clock. Requires confirmation == 1.
 * No library operation resets calibration constants. */
MCFLUKE_API mcfluke_status mcfluke_reset_instrument(
    mcfluke_device *device, int32_t confirmation, mcfluke_error *error);

/* Stateless parsers accept an ID payload or full "QM,<display>" line,
 * without framing ACKs. Display text is preserved; value is in the displayed
 * units (no SI conversion). Non-value states set value to 0; inspect state.
 * Unknown textual displays remain available as READING_TEXT, not fake values. */
MCFLUKE_API mcfluke_status mcfluke_parse_identity(
    const char *response, mcfluke_identity *identity, mcfluke_error *error);
MCFLUKE_API mcfluke_status mcfluke_parse_display(
    const char *response, mcfluke_reading *reading, mcfluke_error *error);

#ifdef __cplusplus
}
#endif

#endif
