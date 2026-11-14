#ifndef MCFLUKE_INTERNAL_H
#define MCFLUKE_INTERNAL_H

#include "mcfluke.h"
#include <stddef.h>
#include <stdint.h>

typedef struct {
    void *handle;
    int fd;
    int simulated;
    int sim_ac;
    int sim_range;
    unsigned char sim_settings[29];
    uint32_t sim_clock_base;
    uint64_t sim_clock_started;
    char pending[256];
    size_t pending_size;
    size_t pending_position;
} mcfluke_transport;

struct mcfluke_device {
    mcfluke_transport transport;
    uint32_t timeout_ms;
};

mcfluke_status mcfluke_fail(mcfluke_error *error, mcfluke_status status,
    int32_t os_code, const char *message);
void mcfluke_clear_error(mcfluke_error *error);
uint64_t mcfluke_now_ms(void);
mcfluke_status mcfluke_transport_open(mcfluke_transport *transport,
    const char *port, mcfluke_error *error);
void mcfluke_transport_close(mcfluke_transport *transport);
mcfluke_status mcfluke_transport_flush(mcfluke_transport *transport, mcfluke_error *error);
mcfluke_status mcfluke_transport_write(mcfluke_transport *transport,
    const char *data, size_t size, uint32_t timeout_ms, mcfluke_error *error);
mcfluke_status mcfluke_transport_read_byte(mcfluke_transport *transport,
    unsigned char *value, uint32_t timeout_ms, mcfluke_error *error);

#endif
