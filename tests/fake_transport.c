#include "internal.h"
#include "fake_transport.h"
#include <string.h>

static unsigned char scripted[1024], active[1024];
static size_t scripted_size, active_size, position;
static int has_script, write_error, flush_error;
static unsigned writes, closes;
static char last_command[64];

void fake_reply_bytes(const unsigned char *bytes, size_t size)
{
    if (size > sizeof(scripted)) size = sizeof(scripted);
    memcpy(scripted, bytes, size);
    scripted_size = size;
    has_script = 1;
}
void fake_reply(const char *text) { fake_reply_bytes((const unsigned char *)text, strlen(text)); }
void fake_write_error(void) { write_error = 1; }
void fake_flush_error(void) { flush_error = 1; }
const char *fake_command(void) { return last_command; }
unsigned fake_write_count(void) { return writes; }
unsigned fake_close_count(void) { return closes; }
uint64_t mcfluke_now_ms(void) { return 1000; }

mcfluke_status mcfluke_transport_open(mcfluke_transport *transport,
    const char *port, mcfluke_error *error)
{
    memset(transport, 0, sizeof(*transport));
    if (strcmp(port, "open-error") == 0)
        return mcfluke_fail(error, MCFLUKE_IO_ERROR, 123, "Injected open error.");
    return MCFLUKE_OK;
}
void mcfluke_transport_close(mcfluke_transport *transport) { (void)transport; ++closes; }
mcfluke_status mcfluke_transport_flush(mcfluke_transport *transport, mcfluke_error *error)
{
    (void)transport;
    if (flush_error) {
        flush_error = 0;
        return mcfluke_fail(error, MCFLUKE_IO_ERROR, 124, "Injected flush error.");
    }
    return MCFLUKE_OK;
}
mcfluke_status mcfluke_transport_write(mcfluke_transport *transport,
    const char *data, size_t size, uint32_t timeout_ms, mcfluke_error *error)
{
    const char *reply;
    (void)transport; (void)timeout_ms;
    ++writes;
    if (size >= sizeof(last_command))
        return mcfluke_fail(error, MCFLUKE_BUFFER_TOO_SMALL, 0, "Test command overflow.");
    memcpy(last_command, data, size);
    last_command[size] = '\0';
    if (write_error) {
        write_error = 0;
        return mcfluke_fail(error, MCFLUKE_IO_ERROR, 125, "Injected write error.");
    }
    if (has_script) {
        memcpy(active, scripted, scripted_size);
        active_size = scripted_size;
        has_script = 0;
    } else {
        reply = strcmp(last_command, "ID\r") == 0 ? "0\rFLUKE 187,V2.02,TEST123\r" :
            strcmp(last_command, "QM\r") == 0 ? "0\rQM,+1.234 V DC\r" : "0\r";
        active_size = strlen(reply);
        memcpy(active, reply, active_size);
    }
    position = 0;
    return MCFLUKE_OK;
}
mcfluke_status mcfluke_transport_read_byte(mcfluke_transport *transport,
    unsigned char *value, uint32_t timeout_ms, mcfluke_error *error)
{
    (void)transport; (void)timeout_ms;
    if (position == active_size)
        return mcfluke_fail(error, MCFLUKE_TIMEOUT, 0, "Injected incomplete reply.");
    *value = active[position++];
    return MCFLUKE_OK;
}
