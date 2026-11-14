#ifndef MCFLUKE_FAKE_TRANSPORT_H
#define MCFLUKE_FAKE_TRANSPORT_H
#include <stddef.h>
void fake_reply(const char *text);
void fake_reply_bytes(const unsigned char *bytes, size_t size);
void fake_write_error(void);
void fake_flush_error(void);
const char *fake_command(void);
unsigned fake_write_count(void);
unsigned fake_close_count(void);
#endif
