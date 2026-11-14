#if defined(__linux__) && !defined(_GNU_SOURCE)
#define _GNU_SOURCE
#endif
#include "internal.h"

#include <ctype.h>
#include <errno.h>
#include <locale.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(__APPLE__)
#include <xlocale.h>
#endif

const char *mcfluke_version(void) { return "0.1.0"; }
uint32_t mcfluke_abi_version(void) { return 1; }

const char *mcfluke_status_message(int32_t status)
{
    switch (status) {
    case MCFLUKE_OK: return "Success";
    case MCFLUKE_INVALID_ARGUMENT: return "Invalid argument";
    case MCFLUKE_IO_ERROR: return "Serial I/O error";
    case MCFLUKE_TIMEOUT: return "Meter response timed out";
    case MCFLUKE_PROTOCOL_ERROR: return "Malformed meter response";
    case MCFLUKE_COMMAND_REJECTED: return "Command rejected in the meter's current mode";
    case MCFLUKE_BUFFER_TOO_SMALL: return "Response exceeds the API buffer capacity";
    case MCFLUKE_WRONG_MODEL: return "This library requires a Fluke 187";
    case MCFLUKE_CONFIRMATION_REQUIRED: return "Explicit confirmation is required";
    case MCFLUKE_UNSUPPORTED: return "Operation is not supported on the Fluke 187";
    case MCFLUKE_OUT_OF_MEMORY: return "Memory allocation failed";
    default: return "Unknown MCFluke status code";
    }
}

void mcfluke_clear_error(mcfluke_error *error)
{
    if (error) memset(error, 0, sizeof(*error));
}

mcfluke_status mcfluke_fail(mcfluke_error *error, mcfluke_status status,
    int32_t os_code, const char *message)
{
    if (error) {
        memset(error, 0, sizeof(*error));
        error->status = status;
        error->os_code = os_code;
        (void)snprintf(error->message, sizeof(error->message), "%s", message);
    }
    return status;
}

static int valid_timeout(uint32_t value) { return value >= 50 && value <= 60000; }

static int copy_field(char *destination, size_t capacity, const char *begin, const char *end)
{
    size_t length;
    while (begin < end && *begin == ' ') ++begin;
    while (end > begin && end[-1] == ' ') --end;
    length = (size_t)(end - begin);
    if (length == 0 || length >= capacity) return 0;
    memcpy(destination, begin, length);
    destination[length] = '\0';
    return 1;
}

static int printable_line(const char *text)
{
    const unsigned char *p = (const unsigned char *)text;
    while (*p) {
        if (*p < 32 || *p > 126) return 0;
        ++p;
    }
    return 1;
}

mcfluke_status mcfluke_parse_identity(const char *response,
    mcfluke_identity *identity, mcfluke_error *error)
{
    const char *first, *second;
    mcfluke_identity result = {{0}, {0}, {0}};
    mcfluke_clear_error(error);
    if (identity) memset(identity, 0, sizeof(*identity));
    if (!response || !identity)
        return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "Identity text and output are required.");
    if (!printable_line(response))
        return mcfluke_fail(error, MCFLUKE_PROTOCOL_ERROR, 0, "Identity contains non-ASCII or framing characters.");
    first = strchr(response, ',');
    second = first ? strchr(first + 1, ',') : NULL;
    if (!first || !second || strchr(second + 1, ',') ||
        !copy_field(result.model, sizeof(result.model), response, first) ||
        !copy_field(result.firmware, sizeof(result.firmware), first + 1, second) ||
        !copy_field(result.serial_number, sizeof(result.serial_number), second + 1, response + strlen(response)) ||
        strncmp(result.model, "FLUKE ", 6) != 0)
        return mcfluke_fail(error, MCFLUKE_PROTOCOL_ERROR, 0, "Expected FLUKE model,firmware,serial number.");
    *identity = result;
    return MCFLUKE_OK;
}

/* Validate the wire grammar; numeric conversion belongs to the C runtime. */
static const char *decimal_token_end(const char *text)
{
    const char *p = text;
    const char *digits;
    int has_digits;
    if (*p == '+' || *p == '-') ++p;
    if (*p == '0' && (p[1] == 'x' || p[1] == 'X')) return NULL;
    digits = p;
    p += strspn(p, "0123456789");
    has_digits = p != digits;
    if (*p == '.') {
        ++p;
        digits = p;
        p += strspn(p, "0123456789");
        has_digits = has_digits || p != digits;
    }
    if (!has_digits) return NULL;
    if (*p == 'e' || *p == 'E') {
        ++p;
        if (*p == '+' || *p == '-') ++p;
        digits = p;
        p += strspn(p, "0123456789");
        if (p == digits) return NULL;
    }
    return p;
}

static mcfluke_status convert_decimal(const char *text, const char *end,
    double *value, mcfluke_error *error)
{
    char *converted_end;
    double number;
    int conversion_error;
#if defined(_WIN32)
    _locale_t locale = _create_locale(LC_NUMERIC, "C");
#else
    locale_t locale = newlocale(LC_NUMERIC_MASK, "C", (locale_t)0);
#endif
    if (!locale)
        return mcfluke_fail(error, MCFLUKE_OUT_OF_MEMORY, errno, "Cannot allocate the C numeric locale.");
    errno = 0;
#if defined(_WIN32)
    number = _strtod_l(text, &converted_end, locale);
    conversion_error = errno;
    _free_locale(locale);
#else
    number = strtod_l(text, &converted_end, locale);
    conversion_error = errno;
    freelocale(locale);
#endif
    if (converted_end != end || !isfinite(number) ||
        (conversion_error == ERANGE && number == 0.0) ||
        (conversion_error != 0 && conversion_error != ERANGE))
        return mcfluke_fail(error, MCFLUKE_PROTOCOL_ERROR, conversion_error,
            "Display number is invalid or outside the representable double range.");
    *value = number;
    return MCFLUKE_OK;
}

static int ascii_prefix_equal(const char *text, const char *prefix)
{
    while (*prefix) {
        if (!*text) return 0;
        if (tolower((unsigned char)*text++) != tolower((unsigned char)*prefix++)) return 0;
    }
    return 1;
}

mcfluke_status mcfluke_parse_display(const char *response,
    mcfluke_reading *reading, mcfluke_error *error)
{
    const char *text, *end, *unit;
    mcfluke_reading result;
    size_t length;
    mcfluke_clear_error(error);
    if (reading) memset(reading, 0, sizeof(*reading));
    if (!response || !reading)
        return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "QM response and reading output are required.");
    if (strncmp(response, "QM,", 3) != 0 || !printable_line(response))
        return mcfluke_fail(error, MCFLUKE_PROTOCOL_ERROR, 0, "Expected one printable ASCII QM,<display> line.");
    text = response + 3;
    length = strlen(text);
    if (length == 0)
        return mcfluke_fail(error, MCFLUKE_PROTOCOL_ERROR, 0, "QM display text is empty.");
    if (length >= sizeof(result.display))
        return mcfluke_fail(error, MCFLUKE_BUFFER_TOO_SMALL, 0, "Display text is too long.");
    memset(&result, 0, sizeof(result));
    memcpy(result.display, text, length + 1);
    while (*text == ' ') ++text;
    if (!*text)
        return mcfluke_fail(error, MCFLUKE_PROTOCOL_ERROR, 0, "QM display text contains only spaces.");
    if (ascii_prefix_equal(text, "Out of Range") &&
        (text[12] == '\0' || text[12] == ' ' || text[12] == '.')) {
        result.state = MCFLUKE_READING_OUT_OF_RANGE;
        unit = text + 12;
        if (*unit == '.') ++unit;
    } else if (isdigit((unsigned char)*text) || *text == '.' ||
        ((*text == '+' || *text == '-') && (isdigit((unsigned char)text[1]) || text[1] == '.'))) {
        mcfluke_status status;
        end = decimal_token_end(text);
        if (!end || !(*end == '\0' || *end == ' ' || isalpha((unsigned char)*end) || *end == '%'))
            return mcfluke_fail(error, MCFLUKE_PROTOCOL_ERROR, 0, "Malformed numeric display response.");
        status = convert_decimal(text, end, &result.value, error);
        if (status != MCFLUKE_OK) return status;
        result.state = MCFLUKE_READING_VALUE;
        unit = end;
    } else {
        result.state = MCFLUKE_READING_TEXT;
        unit = text + strlen(text);
    }
    while (*unit == ' ') ++unit;
    end = unit + strlen(unit);
    while (end > unit && end[-1] == ' ') --end;
    length = (size_t)(end - unit);
    if (length >= sizeof(result.unit))
        return mcfluke_fail(error, MCFLUKE_BUFFER_TOO_SMALL, 0, "Displayed unit is too long.");
    memcpy(result.unit, unit, length);
    result.unit[length] = '\0';
    *reading = result;
    return MCFLUKE_OK;
}

static uint32_t remaining_ms(uint64_t deadline)
{
    uint64_t now = mcfluke_now_ms();
    return deadline > now ? (uint32_t)(deadline - now) : 0;
}

static mcfluke_status read_line(mcfluke_device *device, char *line,
    size_t capacity, uint64_t deadline, mcfluke_error *error)
{
    size_t size = 0;
    unsigned char byte;
    mcfluke_status status;
    for (;;) {
        uint32_t remaining = remaining_ms(deadline);
        if (!remaining)
            return mcfluke_fail(error, MCFLUKE_TIMEOUT, 0,
                "No complete reply before timeout. A control command may already have executed.");
        status = mcfluke_transport_read_byte(&device->transport, &byte, remaining, error);
        if (status != MCFLUKE_OK) return status;
        if (byte == '\n' && size == 0) continue;
        if (byte == '\r') {
            line[size] = '\0';
            return MCFLUKE_OK;
        }
        if (byte < 32 || byte > 126)
            return mcfluke_fail(error, MCFLUKE_PROTOCOL_ERROR, 0, "Non-ASCII byte in meter response.");
        if (size + 1 >= capacity)
            return mcfluke_fail(error, MCFLUKE_BUFFER_TOO_SMALL, 0, "Meter response exceeded its buffer.");
        line[size++] = (char)byte;
    }
}

static mcfluke_status begin_command(mcfluke_device *device, const char *command,
    size_t command_size, uint64_t deadline, mcfluke_error *error)
{
    mcfluke_status status;
    char ack[8];
    mcfluke_clear_error(error);
    if (!device)
        return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "An open device is required.");
    status = mcfluke_transport_flush(&device->transport, error);
    if (status != MCFLUKE_OK) return status;
    status = mcfluke_transport_write(&device->transport, command, command_size,
        remaining_ms(deadline), error);
    if (status != MCFLUKE_OK) return status;
    status = read_line(device, ack, sizeof(ack), deadline, error);
    if (status != MCFLUKE_OK) return status;
    if (strcmp(ack, "1") == 0)
        return mcfluke_fail(error, MCFLUKE_COMMAND_REJECTED, 0, mcfluke_status_message(MCFLUKE_COMMAND_REJECTED));
    if (strcmp(ack, "0") != 0)
        return mcfluke_fail(error, MCFLUKE_PROTOCOL_ERROR, 0, "Expected a single 0 or 1 command acknowledgement.");
    return MCFLUKE_OK;
}

static mcfluke_status transact(mcfluke_device *device, const char *command,
    char *payload, size_t capacity, mcfluke_error *error)
{
    uint64_t deadline = device ? mcfluke_now_ms() + device->timeout_ms : 0;
    mcfluke_status status = begin_command(device, command, strlen(command), deadline, error);
    if (status != MCFLUKE_OK) return status;
    return payload ? read_line(device, payload, capacity, deadline, error) : MCFLUKE_OK;
}

static mcfluke_status read_settings(mcfluke_device *device, unsigned char settings[29],
    uint64_t deadline, mcfluke_error *error)
{
    static const char prefix[] = "QS,";
    mcfluke_status status = begin_command(device, "QS\r", 3, deadline, error);
    size_t i;
    if (status != MCFLUKE_OK) return status;
    for (i = 0; i < 32; ++i) {
        unsigned char byte;
        uint32_t remaining = remaining_ms(deadline);
        if (!remaining)
            return mcfluke_fail(error, MCFLUKE_TIMEOUT, 0, "The binary QS settings response timed out.");
        status = mcfluke_transport_read_byte(&device->transport, &byte, remaining, error);
        if (status != MCFLUKE_OK) return status;
        if (i < 3) {
            if (byte != (unsigned char)prefix[i])
                return mcfluke_fail(error, MCFLUKE_PROTOCOL_ERROR, 0, "Expected the binary QS, settings header.");
        } else settings[i - 3] = byte;
    }
    return MCFLUKE_OK;
}

static uint32_t clock_counter(const unsigned char settings[29])
{
    return (uint32_t)settings[12] | ((uint32_t)settings[13] << 8) |
        ((uint32_t)settings[14] << 16) | ((uint32_t)settings[15] << 24);
}

mcfluke_status mcfluke_get_identity(mcfluke_device *device,
    mcfluke_identity *identity, mcfluke_error *error)
{
    char payload[256];
    mcfluke_status status;
    mcfluke_clear_error(error);
    if (!identity)
        return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "Identity output is required.");
    memset(identity, 0, sizeof(*identity));
    status = transact(device, "ID\r", payload, sizeof(payload), error);
    if (status != MCFLUKE_OK) return status;
    return mcfluke_parse_identity(payload, identity, error);
}

mcfluke_status mcfluke_open(const char *port, uint32_t timeout_ms,
    mcfluke_device **device, mcfluke_error *error)
{
    mcfluke_device *result;
    mcfluke_identity identity;
    mcfluke_status status;
    mcfluke_clear_error(error);
    if (device) *device = NULL;
    if (!port || !*port || !device || !valid_timeout(timeout_ms))
        return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0,
            "Port, device output and timeout in 50..60000 ms are required.");
    result = (mcfluke_device *)calloc(1, sizeof(*result));
    if (!result)
        return mcfluke_fail(error, MCFLUKE_OUT_OF_MEMORY, 0, mcfluke_status_message(MCFLUKE_OUT_OF_MEMORY));
    result->timeout_ms = timeout_ms;
    status = mcfluke_transport_open(&result->transport, port, error);
    if (status == MCFLUKE_OK) {
        status = mcfluke_get_identity(result, &identity, error);
        if (status == MCFLUKE_OK && strcmp(identity.model, "FLUKE 187") != 0)
            status = mcfluke_fail(error, MCFLUKE_WRONG_MODEL, 0,
                "The connected instrument did not identify itself as FLUKE 187.");
        if (status != MCFLUKE_OK) mcfluke_transport_close(&result->transport);
    }
    if (status != MCFLUKE_OK) {
        free(result);
        return status;
    }
    *device = result;
    return MCFLUKE_OK;
}

void mcfluke_close(mcfluke_device *device)
{
    if (!device) return;
    mcfluke_transport_close(&device->transport);
    free(device);
}

mcfluke_status mcfluke_read_display(mcfluke_device *device,
    mcfluke_reading *reading, mcfluke_error *error)
{
    char payload[256];
    mcfluke_status status;
    mcfluke_clear_error(error);
    if (!reading)
        return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "Reading output is required.");
    memset(reading, 0, sizeof(*reading));
    status = transact(device, "QM\r", payload, sizeof(payload), error);
    if (status != MCFLUKE_OK) return status;
    return mcfluke_parse_display(payload, reading, error);
}

mcfluke_status mcfluke_get_clock(mcfluke_device *device,
    uint32_t *counter, mcfluke_error *error)
{
    unsigned char settings[29];
    mcfluke_status status;
    mcfluke_clear_error(error);
    if (counter) *counter = 0;
    if (!device || !counter)
        return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "Device and clock-counter output are required.");
    status = read_settings(device, settings, mcfluke_now_ms() + device->timeout_ms, error);
    if (status != MCFLUKE_OK) return status;
    *counter = clock_counter(settings);
    return MCFLUKE_OK;
}

mcfluke_status mcfluke_set_clock(mcfluke_device *device, uint32_t time_of_day_ticks,
    uint32_t *written_counter, mcfluke_error *error)
{
    unsigned char settings[29], command[33];
    static const int offsets[] = {0, -1, 1};
    uint64_t deadline;
    uint32_t current, target;
    int64_t day, best = -1, best_distance = INT64_MAX;
    size_t i;
    mcfluke_status status;
    mcfluke_clear_error(error);
    if (written_counter) *written_counter = 0;
    if (!device || time_of_day_ticks >= MCFLUKE_CLOCK_TICKS_PER_DAY)
        return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0,
            "Device and time-of-day in 0..863999 deciseconds are required.");
    deadline = mcfluke_now_ms() + device->timeout_ms;
    status = read_settings(device, settings, deadline, error);
    if (status != MCFLUKE_OK) return status;
    current = clock_counter(settings);
    day = (int64_t)(current / MCFLUKE_CLOCK_TICKS_PER_DAY) * MCFLUKE_CLOCK_TICKS_PER_DAY;
    for (i = 0; i < sizeof(offsets) / sizeof(offsets[0]); ++i) {
        int64_t candidate = day + (int64_t)offsets[i] * MCFLUKE_CLOCK_TICKS_PER_DAY + time_of_day_ticks;
        int64_t distance;
        if (candidate < 0 || candidate > UINT32_MAX) continue;
        distance = llabs(candidate - (int64_t)current);
        if (distance < best_distance) { best = candidate; best_distance = distance; }
    }
    if (best < 0)
        return mcfluke_fail(error, MCFLUKE_PROTOCOL_ERROR, 0, "The target clock counter cannot be represented.");
    target = (uint32_t)best;
    memcpy(command, "PS ", 3);
    memcpy(command + 3, settings, sizeof(settings));
    for (i = 0; i < 4; ++i) command[3 + 12 + i] = (unsigned char)(target >> (8 * i));
    command[32] = '\r';
    status = begin_command(device, (const char *)command, sizeof(command), deadline, error);
    if (status == MCFLUKE_OK && written_counter) *written_counter = target;
    return status;
}

mcfluke_status mcfluke_get_timeout(mcfluke_device *device,
    uint32_t *timeout_ms, mcfluke_error *error)
{
    mcfluke_clear_error(error);
    if (!device || !timeout_ms)
        return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "Device and timeout output are required.");
    *timeout_ms = device->timeout_ms;
    return MCFLUKE_OK;
}

mcfluke_status mcfluke_set_timeout(mcfluke_device *device,
    uint32_t timeout_ms, mcfluke_error *error)
{
    mcfluke_clear_error(error);
    if (!device || !valid_timeout(timeout_ms))
        return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "Device and timeout in 50..60000 ms are required.");
    device->timeout_ms = timeout_ms;
    return MCFLUKE_OK;
}

mcfluke_status mcfluke_press_button(mcfluke_device *device, int32_t button,
    int32_t confirm_calibration, mcfluke_error *error)
{
    char command[16];
    mcfluke_clear_error(error);
    if (!device || !((button >= 10 && button <= 23) || (button >= 27 && button <= 30)))
        return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "A device and a documented SF button code are required.");
    if (button == MCFLUKE_LOGGING || button == MCFLUKE_SAVE)
        return mcfluke_fail(error, MCFLUKE_UNSUPPORTED, 0, "Logging and Save are Fluke 189 features, not Fluke 187 features.");
    if (button == MCFLUKE_CALIBRATION && confirm_calibration != 1)
        return mcfluke_fail(error, MCFLUKE_CONFIRMATION_REQUIRED, 0,
            "Calibration entry requires explicit confirmation. Consult the Fluke calibration manual.");
    (void)snprintf(command, sizeof(command), "SF %02d\r", (int)button);
    return transact(device, command, NULL, 0, error);
}

mcfluke_status mcfluke_default_setup(mcfluke_device *device, mcfluke_error *error)
{
    return transact(device, "DS\r", NULL, 0, error);
}

mcfluke_status mcfluke_reset_instrument(mcfluke_device *device,
    int32_t confirmation, mcfluke_error *error)
{
    mcfluke_clear_error(error);
    if (!device)
        return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "An open device is required.");
    if (confirmation != 1)
        return mcfluke_fail(error, MCFLUKE_CONFIRMATION_REQUIRED, 0,
            "Factory reset erases settings and resets the clock; explicit confirmation is required.");
    return transact(device, "RI\r", NULL, 0, error);
}
