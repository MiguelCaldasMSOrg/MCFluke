#include "mcfluke.h"
#include <errno.h>
#include <inttypes.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void json_string(const char *text)
{
    const unsigned char *p = (const unsigned char *)text;
    putchar('"');
    for (; *p; ++p) {
        if (*p == '"' || *p == '\\') { putchar('\\'); putchar(*p); }
        else if (*p < 32) printf("\\u%04x", (unsigned)*p);
        else putchar(*p);
    }
    putchar('"');
}

static void json_error(const mcfluke_error *error)
{
    printf("\"error\":{\"status\":%" PRId32 ",\"osCode\":%" PRId32 ",\"message\":",
        error->status, error->os_code);
    json_string(error->message);
    putchar('}');
}

static void invalid(mcfluke_error *error, const char *message)
{
    memset(error, 0, sizeof(*error));
    error->status = MCFLUKE_INVALID_ARGUMENT;
    (void)snprintf(error->message, sizeof(error->message), "%s", message);
}

static int read_request(char *line, size_t capacity, mcfluke_error *error)
{
    size_t length = 0;
    int byte, malformed = 0, oversized = 0;
    while ((byte = getchar()) != EOF && byte != '\n') {
        if (byte == 0 || byte == '\r') malformed = 1;
        if (length + 2 >= capacity) oversized = 1;
        else line[length++] = (char)byte;
    }
    if (byte == EOF && length == 0 && !oversized) return 0;
    line[length] = '\0';
    if (oversized || byte == EOF) {
        invalid(error, "Request must fit in 1023 bytes and end in LF.");
        return -1;
    }
    if (malformed) {
        invalid(error, "Requests may not contain NUL or CR; use LF line endings.");
        return -1;
    }
    return 1;
}

static int parse_u32(const char *text, uint32_t *value)
{
    char *end;
    unsigned long parsed;
    if (!text || *text < '0' || *text > '9') return 0;
    errno = 0;
    parsed = strtoul(text, &end, 10);
    if (errno || *end || parsed > UINT32_MAX) return 0;
    *value = (uint32_t)parsed;
    return 1;
}

static int parse_i32(const char *text, int32_t *value)
{
    char *end;
    long long parsed;
    if (!text || (!(*text >= '0' && *text <= '9') && *text != '-')) return 0;
    errno = 0;
    parsed = strtoll(text, &end, 10);
    if (end == text || errno || *end || parsed < INT32_MIN || parsed > INT32_MAX) return 0;
    *value = (int32_t)parsed;
    return 1;
}

static void json_identity(const mcfluke_identity *identity)
{
    fputs("{\"model\":", stdout); json_string(identity->model);
    fputs(",\"firmware\":", stdout); json_string(identity->firmware);
    fputs(",\"serialNumber\":", stdout); json_string(identity->serial_number);
    putchar('}');
}

static void json_reading(const mcfluke_reading *reading)
{
    fputs("{\"display\":", stdout); json_string(reading->display);
    fputs(",\"unit\":", stdout); json_string(reading->unit);
    printf(",\"state\":%" PRId32 ",\"value\":", reading->state);
    if (reading->state == MCFLUKE_READING_VALUE) printf("%.17g", reading->value);
    else fputs("null", stdout);
    putchar('}');
}

int main(int argc, char **argv)
{
    const char *port = "COM3";
    uint32_t timeout = 1500;
    mcfluke_device *device = NULL;
    mcfluke_error error;
    char line[1024];
    int i, result = EXIT_SUCCESS;
    for (i = 1; i < argc; ++i) {
        if (strcmp(argv[i], "--help") == 0) {
            puts("Usage: mcfluke-host [--port COM3|sim://187] [--timeout MILLISECONDS]");
            puts("Persistent JSON-lines bridge; see docs/bridge.md.");
            return EXIT_SUCCESS;
        }
        if (i + 1 >= argc) { fputs("Missing option value.\n", stderr); return 2; }
        if (strcmp(argv[i], "--port") == 0) port = argv[++i];
        else if (strcmp(argv[i], "--timeout") == 0) {
            if (!parse_u32(argv[++i], &timeout)) { fputs("Invalid timeout.\n", stderr); return 2; }
        } else { fprintf(stderr, "Unknown option: %s\n", argv[i]); return 2; }
    }
    if (mcfluke_open(port, timeout, &device, &error) != MCFLUKE_OK) {
        fputs("{\"type\":\"ready\",\"ok\":false,", stdout);
        json_error(&error);
        puts("}");
        fflush(stdout);
        return EXIT_FAILURE;
    }
    puts("{\"type\":\"ready\",\"ok\":true}");
    fflush(stdout);
    for (;;) {
        char *id_text, *verb, *argument, *separator, *end;
        uint64_t id = 0;
        uint32_t timeout_value = 0;
        int32_t integer = 0, confirmation = 0;
        mcfluke_identity identity;
        mcfluke_reading reading;
        mcfluke_status status = MCFLUKE_OK;
        enum { NULL_RESULT, ID_RESULT, READING_RESULT, NUMBER_RESULT, VERSION_RESULT, STATUS_RESULT } kind = NULL_RESULT;
        int close_requested = 0;
        int request_state;
        memset(&error, 0, sizeof(error));
        request_state = read_request(line, sizeof(line), &error);
        if (request_state == 0) break;
        if (request_state < 0) {
            printf("{\"id\":0,\"ok\":false,"); json_error(&error); puts("}");
            fflush(stdout);
            continue;
        }
        id_text = line;
        separator = strchr(id_text, '\t');
        verb = separator ? separator + 1 : NULL;
        if (separator) *separator = '\0';
        separator = verb ? strchr(verb, '\t') : NULL;
        argument = separator ? separator + 1 : NULL;
        if (separator) *separator = '\0';
        errno = 0;
        if (*id_text >= '0' && *id_text <= '9') id = strtoull(id_text, &end, 10);
        else end = id_text;
        if (end == id_text || *end || errno || !id || id > UINT64_C(9007199254740991) ||
            !verb || !argument || strchr(argument, '\t')) {
            invalid(&error, "Expected positive request-id<TAB>verb<TAB>argument.");
            id = 0;
            status = MCFLUKE_INVALID_ARGUMENT;
        } else if (strcmp(verb, "id") == 0 && !*argument) {
            status = mcfluke_get_identity(device, &identity, &error);
            kind = ID_RESULT;
        } else if (strcmp(verb, "display") == 0 && !*argument) {
            status = mcfluke_read_display(device, &reading, &error);
            kind = READING_RESULT;
        } else if (strcmp(verb, "clock") == 0 && !*argument) {
            status = mcfluke_get_clock(device, &timeout_value, &error);
            kind = NUMBER_RESULT;
        } else if (strcmp(verb, "set-clock") == 0 && parse_u32(argument, &timeout_value)) {
            status = mcfluke_set_clock(device, timeout_value, &timeout_value, &error);
            kind = NUMBER_RESULT;
        } else if (strcmp(verb, "parse-id") == 0) {
            status = mcfluke_parse_identity(argument, &identity, &error);
            kind = ID_RESULT;
        } else if (strcmp(verb, "parse-display") == 0) {
            status = mcfluke_parse_display(argument, &reading, &error);
            kind = READING_RESULT;
        } else if (strcmp(verb, "button") == 0) {
            char *space = strchr(argument, ' ');
            if (space) *space++ = '\0';
            if (!space || !parse_i32(argument, &integer) || !parse_i32(space, &confirmation)) {
                invalid(&error, "Button requires an integer code and a confirmation flag.");
                status = MCFLUKE_INVALID_ARGUMENT;
            } else status = mcfluke_press_button(device, integer, confirmation, &error);
        } else if (strcmp(verb, "defaults") == 0 && !*argument) {
            status = mcfluke_default_setup(device, &error);
        } else if (strcmp(verb, "reset") == 0 && parse_i32(argument, &confirmation)) {
            status = mcfluke_reset_instrument(device, confirmation, &error);
        } else if (strcmp(verb, "get-timeout") == 0 && !*argument) {
            status = mcfluke_get_timeout(device, &timeout_value, &error);
            kind = NUMBER_RESULT;
        } else if (strcmp(verb, "set-timeout") == 0 && parse_u32(argument, &timeout_value)) {
            status = mcfluke_set_timeout(device, timeout_value, &error);
        } else if (strcmp(verb, "version") == 0 && !*argument) {
            kind = VERSION_RESULT;
        } else if (strcmp(verb, "status-message") == 0 && parse_i32(argument, &integer)) {
            kind = STATUS_RESULT;
        } else if (strcmp(verb, "close") == 0 && !*argument) {
            close_requested = 1;
        } else {
            invalid(&error, "Unknown verb or invalid arguments.");
            status = MCFLUKE_INVALID_ARGUMENT;
        }
        printf("{\"id\":%" PRIu64 ",\"ok\":%s,", id, status == MCFLUKE_OK ? "true" : "false");
        if (status != MCFLUKE_OK) json_error(&error);
        else {
            fputs("\"result\":", stdout);
            switch (kind) {
            case ID_RESULT: json_identity(&identity); break;
            case READING_RESULT: json_reading(&reading); break;
            case NUMBER_RESULT: printf("%" PRIu32, timeout_value); break;
            case VERSION_RESULT:
                fputs("{\"version\":", stdout); json_string(mcfluke_version());
                printf(",\"abi\":%" PRIu32 "}", mcfluke_abi_version()); break;
            case STATUS_RESULT: json_string(mcfluke_status_message(integer)); break;
            default: fputs("null", stdout); break;
            }
        }
        puts("}");
        if (fflush(stdout) != 0) { result = EXIT_FAILURE; break; }
        if (close_requested) break;
    }
    if (ferror(stdin)) { fputs("Failed to read bridge input.\n", stderr); result = EXIT_FAILURE; }
    mcfluke_close(device);
    return result;
}
