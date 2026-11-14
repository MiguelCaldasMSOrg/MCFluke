#include "mcfluke.h"
#include "fake_transport.h"
#include <locale.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int failures, checks;
#define CHECK(expression) do { ++checks; if (!(expression)) { \
    fprintf(stderr, "%s:%d: %s\n", __FILE__, __LINE__, #expression); ++failures; } } while (0)

_Static_assert(sizeof(mcfluke_error) == 264, "Error ABI layout");
_Static_assert(sizeof(mcfluke_identity) == 128, "Identity ABI layout");
_Static_assert(sizeof(mcfluke_reading) == 176, "Reading ABI layout");

static void parsers(void)
{
    mcfluke_identity identity;
    mcfluke_reading reading;
    mcfluke_error error;
    const char *units[] = {
        "V AC", "mV AC", "V DC", "mV DC", "V AC+DC", "mV AC+DC",
        "Ohms", "kOhms", "MOhms", "nS", "nFarads", "uFarads", "mFarads",
        "A AC", "mA AC", "uA AC", "A DC", "mA DC", "uA DC", "A AC+DC",
        "Deg C", "Deg F", "dBm", "dBV", "Hz", "kHz", "%", "mS"
    };
    size_t i;
    char text[400];
    CHECK(mcfluke_abi_version() == 1);
    CHECK(strcmp(mcfluke_version(), "0.1.0") == 0);
    for (i = 0; i <= MCFLUKE_OUT_OF_MEMORY; ++i) CHECK(*mcfluke_status_message((int32_t)i) != '\0');
    CHECK(strstr(mcfluke_status_message(-1), "Unknown") != NULL);
    CHECK(mcfluke_parse_identity("FLUKE 187, V2.02, 123456789", &identity, &error) == MCFLUKE_OK);
    CHECK(strcmp(identity.model, "FLUKE 187") == 0);
    CHECK(strcmp(identity.firmware, "V2.02") == 0);
    CHECK(strcmp(identity.serial_number, "123456789") == 0);
    CHECK(mcfluke_parse_identity("FLUKE 187,,123", &identity, &error) == MCFLUKE_PROTOCOL_ERROR);
    CHECK(mcfluke_parse_identity("OTHER,V1,123", &identity, &error) == MCFLUKE_PROTOCOL_ERROR);
    CHECK(mcfluke_parse_identity("FLUKE 187,V1,123,extra", &identity, &error) == MCFLUKE_PROTOCOL_ERROR);
    CHECK(mcfluke_parse_identity("FLUKE 187,V1,123\r", &identity, &error) == MCFLUKE_PROTOCOL_ERROR);
    CHECK(mcfluke_parse_identity(NULL, &identity, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_parse_identity("x", NULL, &error) == MCFLUKE_INVALID_ARGUMENT);
    for (i = 0; i < sizeof(units) / sizeof(units[0]); ++i) {
        (void)snprintf(text, sizeof(text), "QM,-12.345 %s", units[i]);
        CHECK(mcfluke_parse_display(text, &reading, &error) == MCFLUKE_OK);
        CHECK(fabs(reading.value + 12.345) < 1e-10);
        CHECK(reading.state == MCFLUKE_READING_VALUE);
        CHECK(strcmp(reading.unit, units[i]) == 0);
        CHECK(strcmp(reading.display, text + 3) == 0);
    }
    CHECK(mcfluke_parse_display("QM,-121.43VDC", &reading, &error) == MCFLUKE_OK);
    CHECK(strcmp(reading.unit, "VDC") == 0);
    CHECK(mcfluke_parse_display("QM,  +1.25e-3 mV AC  ", &reading, &error) == MCFLUKE_OK);
    CHECK(fabs(reading.value - 0.00125) < 1e-12);
    CHECK(strcmp(reading.display, "  +1.25e-3 mV AC  ") == 0);
    CHECK(strcmp(reading.unit, "mV AC") == 0);
    CHECK(mcfluke_parse_display("QM,Out of Range mVDC", &reading, &error) == MCFLUKE_OK);
    CHECK(reading.state == MCFLUKE_READING_OUT_OF_RANGE);
    CHECK(strcmp(reading.unit, "mVDC") == 0);
    CHECK(mcfluke_parse_display("QM,Out of range", &reading, &error) == MCFLUKE_OK);
    CHECK(reading.state == MCFLUKE_READING_OUT_OF_RANGE);
    CHECK(mcfluke_parse_display("QM,Out of range. mV AC", &reading, &error) == MCFLUKE_OK);
    CHECK(reading.state == MCFLUKE_READING_OUT_OF_RANGE && strcmp(reading.unit, "mV AC") == 0);
    CHECK(mcfluke_parse_display("QM,----", &reading, &error) == MCFLUKE_OK);
    CHECK(reading.state == MCFLUKE_READING_TEXT);
    CHECK(mcfluke_parse_display("QM,Lo", &reading, &error) == MCFLUKE_OK);
    CHECK(reading.state == MCFLUKE_READING_TEXT);
    CHECK(mcfluke_parse_display("QM,V", &reading, &error) == MCFLUKE_OK);
    CHECK(reading.state == MCFLUKE_READING_TEXT);
    CHECK(mcfluke_parse_display("QM,0", &reading, &error) == MCFLUKE_OK);
    CHECK(reading.state == MCFLUKE_READING_VALUE && reading.value == 0);
    CHECK(mcfluke_parse_display("QM,.5 %", &reading, &error) == MCFLUKE_OK);
    CHECK(reading.value == 0.5);
    CHECK(mcfluke_parse_display("QM,1.2.3 V", &reading, &error) == MCFLUKE_PROTOCOL_ERROR);
    CHECK(mcfluke_parse_display("QM,+1e999 V", &reading, &error) == MCFLUKE_PROTOCOL_ERROR);
    CHECK(mcfluke_parse_display("QM,+1e V", &reading, &error) == MCFLUKE_PROTOCOL_ERROR);
    CHECK(mcfluke_parse_display("QM,+1e-999 V", &reading, &error) == MCFLUKE_PROTOCOL_ERROR);
    CHECK(mcfluke_parse_display("QM,", &reading, &error) == MCFLUKE_PROTOCOL_ERROR);
    CHECK(mcfluke_parse_display("QM,   ", &reading, &error) == MCFLUKE_PROTOCOL_ERROR);
    CHECK(mcfluke_parse_display("QM,+1 V\n", &reading, &error) == MCFLUKE_PROTOCOL_ERROR);
    CHECK(mcfluke_parse_display("XX,+1 V", &reading, &error) == MCFLUKE_PROTOCOL_ERROR);
    CHECK(mcfluke_parse_display(NULL, &reading, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_parse_display("QM,1", NULL, &error) == MCFLUKE_INVALID_ARGUMENT);
    memset(text, 'x', sizeof(text));
    memcpy(text, "QM,", 3);
    text[sizeof(text) - 1] = '\0';
    CHECK(mcfluke_parse_display(text, &reading, &error) == MCFLUKE_BUFFER_TOO_SMALL);
    memset(text, 'x', 90);
    memcpy(text, "QM,+1 ", 6);
    text[90] = '\0';
    CHECK(mcfluke_parse_display(text, &reading, &error) == MCFLUKE_BUFFER_TOO_SMALL);
    CHECK(error.status == MCFLUKE_BUFFER_TOO_SMALL && error.message[0] != '\0');
}

static void protocol(void)
{
    mcfluke_device *device = NULL;
    mcfluke_identity identity;
    mcfluke_reading reading;
    mcfluke_error error;
    uint32_t timeout;
    unsigned before;
    int32_t buttons[] = {10,11,12,13,14,15,16,17,18,19,21,22,27,28,29};
    size_t i;
    CHECK(mcfluke_open("test", 1500, &device, &error) == MCFLUKE_OK);
    if (!device) { fputs("Cannot continue device tests.\n", stderr); exit(1); }
    CHECK(strcmp(fake_command(), "ID\r") == 0);
    CHECK(mcfluke_get_identity(device, &identity, &error) == MCFLUKE_OK);
    CHECK(strcmp(identity.model, "FLUKE 187") == 0);
    CHECK(mcfluke_read_display(device, &reading, &error) == MCFLUKE_OK);
    CHECK(strcmp(fake_command(), "QM\r") == 0);
    CHECK(reading.value == 1.234);
    CHECK(mcfluke_get_timeout(device, &timeout, &error) == MCFLUKE_OK && timeout == 1500);
    CHECK(mcfluke_set_timeout(device, 50, &error) == MCFLUKE_OK);
    CHECK(mcfluke_get_timeout(device, &timeout, &error) == MCFLUKE_OK && timeout == 50);
    CHECK(mcfluke_set_timeout(device, 60000, &error) == MCFLUKE_OK);
    CHECK(mcfluke_set_timeout(device, 49, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_set_timeout(device, 60001, &error) == MCFLUKE_INVALID_ARGUMENT);
    for (i = 0; i < sizeof(buttons) / sizeof(buttons[0]); ++i) {
        CHECK(mcfluke_press_button(device, buttons[i], 0, &error) == MCFLUKE_OK);
        CHECK(strncmp(fake_command(), "SF ", 3) == 0);
    }
    before = fake_write_count();
    CHECK(mcfluke_press_button(device, 20, 0, &error) == MCFLUKE_CONFIRMATION_REQUIRED);
    CHECK(mcfluke_press_button(device, 20, 2, &error) == MCFLUKE_CONFIRMATION_REQUIRED);
    CHECK(mcfluke_press_button(device, 23, 0, &error) == MCFLUKE_UNSUPPORTED);
    CHECK(mcfluke_press_button(device, 30, 0, &error) == MCFLUKE_UNSUPPORTED);
    CHECK(mcfluke_press_button(device, 24, 0, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_press_button(device, -1, 0, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_reset_instrument(device, 0, &error) == MCFLUKE_CONFIRMATION_REQUIRED);
    CHECK(fake_write_count() == before);
    CHECK(mcfluke_press_button(device, 20, 1, &error) == MCFLUKE_OK);
    CHECK(strcmp(fake_command(), "SF 20\r") == 0);
    CHECK(mcfluke_reset_instrument(device, 1, &error) == MCFLUKE_OK);
    CHECK(strcmp(fake_command(), "RI\r") == 0);
    CHECK(mcfluke_default_setup(device, &error) == MCFLUKE_OK);
    CHECK(strcmp(fake_command(), "DS\r") == 0);
    fake_reply("1\r");
    CHECK(mcfluke_default_setup(device, &error) == MCFLUKE_COMMAND_REJECTED);
    fake_reply("2\r");
    CHECK(mcfluke_default_setup(device, &error) == MCFLUKE_PROTOCOL_ERROR);
    fake_reply("00\r");
    CHECK(mcfluke_default_setup(device, &error) == MCFLUKE_PROTOCOL_ERROR);
    fake_reply("00000000\r");
    CHECK(mcfluke_default_setup(device, &error) == MCFLUKE_BUFFER_TOO_SMALL);
    fake_reply("");
    CHECK(mcfluke_default_setup(device, &error) == MCFLUKE_TIMEOUT);
    fake_reply("0\rQM,+12");
    CHECK(mcfluke_read_display(device, &reading, &error) == MCFLUKE_TIMEOUT);
    fake_reply("0\r\nQM,+12.5 V DC\r\n");
    CHECK(mcfluke_read_display(device, &reading, &error) == MCFLUKE_OK);
    CHECK(reading.value == 12.5);
    fake_reply("0\rID,+12.5 V DC\r");
    CHECK(mcfluke_read_display(device, &reading, &error) == MCFLUKE_PROTOCOL_ERROR);
    {
        const unsigned char bytes[] = {'0','\r','Q','M',',',0,'\r'};
        fake_reply_bytes(bytes, sizeof(bytes));
        CHECK(mcfluke_read_display(device, &reading, &error) == MCFLUKE_PROTOCOL_ERROR);
    }
    fake_write_error();
    CHECK(mcfluke_default_setup(device, &error) == MCFLUKE_IO_ERROR);
    CHECK(error.os_code == 125);
    before = fake_write_count();
    fake_flush_error();
    CHECK(mcfluke_default_setup(device, &error) == MCFLUKE_IO_ERROR);
    CHECK(fake_write_count() == before);
    CHECK(mcfluke_read_display(device, &reading, &error) == MCFLUKE_OK);
    CHECK(error.status == MCFLUKE_OK && error.os_code == 0 && error.message[0] == '\0');
    CHECK(mcfluke_get_identity(device, NULL, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_read_display(device, NULL, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_get_timeout(device, NULL, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_read_display(NULL, &reading, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_get_timeout(NULL, &timeout, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_set_timeout(NULL, 1500, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_press_button(NULL, 11, 0, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_default_setup(NULL, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_reset_instrument(NULL, 1, &error) == MCFLUKE_INVALID_ARGUMENT);
    mcfluke_close(device);
    mcfluke_close(NULL);
    device = NULL;
    before = fake_close_count();
    fake_reply("0\rFLUKE 189,V1,123\r");
    CHECK(mcfluke_open("test", 1500, &device, &error) == MCFLUKE_WRONG_MODEL);
    CHECK(device == NULL && fake_close_count() == before + 1);
    fake_reply("1\r");
    CHECK(mcfluke_open("test", 1500, &device, &error) == MCFLUKE_COMMAND_REJECTED);
    CHECK(device == NULL);
    CHECK(mcfluke_open("open-error", 1500, &device, &error) == MCFLUKE_IO_ERROR);
    CHECK(device == NULL && error.os_code == 123);
    CHECK(mcfluke_open(NULL, 1500, &device, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_open("", 1500, &device, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_open("test", 49, &device, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_open("test", 1500, NULL, &error) == MCFLUKE_INVALID_ARGUMENT);
    CHECK(mcfluke_parse_display("QM,+1 V", &reading, NULL) == MCFLUKE_OK);
}

static void decimal_conversion(void)
{
    const char *numbers[] = {
        "1.656", "1.676", "1.632", "1.425", "1.423", "0.1", "1.005",
        "+01.440", "-1.656", "-0", "1.25e-3", "1.7976931348623157e308",
        "2.2250738585072014e-308", "4.9406564584124654e-324",
        "1.00000000000000011102230246251565404236316680908203125",
        "1.00000000000000011102230246251565404236316680908203126"
    };
    const char *comma_locales[] = {
        "de_DE.UTF-8", "fr_FR.UTF-8", "pt_PT.UTF-8", "de-DE",
        "German_Germany.1252", "Portuguese_Portugal.1252"
    };
    const char *original = setlocale(LC_NUMERIC, NULL);
    char saved_locale[256], selected_locale[256], response[160];
    mcfluke_reading reading;
    mcfluke_error error;
    size_t i;
    int found_comma_locale = 0;
    CHECK(original != NULL && strlen(original) < sizeof(saved_locale));
    if (!original || strlen(original) >= sizeof(saved_locale)) return;
    (void)snprintf(saved_locale, sizeof(saved_locale), "%s", original);
    CHECK(setlocale(LC_NUMERIC, "C") != NULL);
    for (i = 0; i < sizeof(numbers) / sizeof(numbers[0]); ++i) {
        double expected = strtod(numbers[i], NULL);
        (void)snprintf(response, sizeof(response), "QM,%s mV AC", numbers[i]);
        CHECK(mcfluke_parse_display(response, &reading, &error) == MCFLUKE_OK);
        CHECK(reading.value == expected);
        CHECK(!!signbit(reading.value) == !!signbit(expected));
        CHECK(strcmp(reading.display, response + 3) == 0);
    }
    CHECK(mcfluke_parse_display("QM,+0x1p2 V", &reading, &error) == MCFLUKE_PROTOCOL_ERROR);
    CHECK(mcfluke_parse_display("QM,+1e+ V", &reading, &error) == MCFLUKE_PROTOCOL_ERROR);
    CHECK(mcfluke_parse_display("QM,+1,656 V", &reading, &error) == MCFLUKE_PROTOCOL_ERROR);
    for (i = 0; i < sizeof(comma_locales) / sizeof(comma_locales[0]); ++i) {
        const char *selected = setlocale(LC_NUMERIC, comma_locales[i]);
        if (!selected) continue;
        (void)snprintf(selected_locale, sizeof(selected_locale), "%s", selected);
        CHECK(strcmp(localeconv()->decimal_point, ",") == 0);
        CHECK(mcfluke_parse_display("QM,+1.656 mV AC", &reading, &error) == MCFLUKE_OK);
        CHECK(reading.value == 1.656);
        CHECK(strcmp(setlocale(LC_NUMERIC, NULL), selected_locale) == 0);
        found_comma_locale = 1;
        break;
    }
    if (!found_comma_locale)
        puts("SKIP comma-decimal locale check: no requested locale is installed.");
    CHECK(setlocale(LC_NUMERIC, saved_locale) != NULL);
}

int main(void)
{
    parsers();
    decimal_conversion();
    protocol();
    printf("%d checks, %d failures\n", checks, failures);
    return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
