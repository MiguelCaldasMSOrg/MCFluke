#include "mcfluke.h"
#include <errno.h>
#include <math.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

static volatile sig_atomic_t running = 1;
static void interrupted(int signal_number) { (void)signal_number; running = 0; }

static void usage(void)
{
    puts("Usage: mcfluke-example [--port COM3|sim://187] [--count N] [--interval SECONDS]");
    puts("Read-only: prints identification and the primary display every 2 seconds by default.");
    puts("A count of zero runs until Ctrl+C. No control commands are sent.");
}

int main(int argc, char **argv)
{
    const char *port = "COM3";
    unsigned long count = 0, index = 0;
    double interval = 2.0;
    uint64_t next;
    int i, result = EXIT_SUCCESS;
    mcfluke_device *device = NULL;
    mcfluke_identity identity;
    mcfluke_reading reading;
    mcfluke_error error;
    for (i = 1; i < argc; ++i) {
        char *end;
        if (strcmp(argv[i], "--help") == 0) { usage(); return EXIT_SUCCESS; }
        if (i + 1 >= argc) { fputs("Missing option value.\n", stderr); return 2; }
        if (strcmp(argv[i], "--port") == 0) port = argv[++i];
        else if (strcmp(argv[i], "--count") == 0) {
            const char *text = argv[++i];
            errno = 0;
            count = strtoul(text, &end, 10);
            if (!*text || *text == '-' || *end || errno || count > 1000000) {
                fputs("Count must be an integer in 0..1000000.\n", stderr);
                return 2;
            }
        } else if (strcmp(argv[i], "--interval") == 0) {
            interval = strtod(argv[++i], &end);
            if (*end || !isfinite(interval) || interval < 0.001 || interval > 3600) {
                fputs("Interval must be 0.001..3600 seconds.\n", stderr);
                return 2;
            }
        } else {
            fprintf(stderr, "Unknown option: %s\n", argv[i]);
            return 2;
        }
    }
    (void)signal(SIGINT, interrupted);
    if (mcfluke_open(port, 1500, &device, &error) != MCFLUKE_OK) {
        fprintf(stderr, "Open failed: %s\n", error.message);
        return EXIT_FAILURE;
    }
    if (mcfluke_get_identity(device, &identity, &error) != MCFLUKE_OK) {
        fprintf(stderr, "Identification failed: %s\n", error.message);
        mcfluke_close(device);
        return EXIT_FAILURE;
    }
    printf("%s | firmware %s | serial %s | %s\n",
        identity.model, identity.firmware, identity.serial_number, port);
    fflush(stdout);
    next = GetTickCount64();
    while (running && (!count || index < count)) {
        uint64_t now;
        while (running && (now = GetTickCount64()) < next)
            Sleep((DWORD)(next - now > 100 ? 100 : next - now));
        if (!running) break;
        if (mcfluke_read_display(device, &reading, &error) != MCFLUKE_OK) {
            fprintf(stderr, "Read failed: %s\n", error.message);
            result = EXIT_FAILURE;
            break;
        }
        puts(reading.display);
        fflush(stdout);
        ++index;
        next += (uint64_t)(interval * 1000);
        now = GetTickCount64();
        if (next < now) next = now;
    }
    mcfluke_close(device);
    return result;
}
