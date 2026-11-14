#ifndef MCFLUKE_FAKE_TRANSPORT_H
#define MCFLUKE_FAKE_TRANSPORT_H
#include <stddef.h>
#include <stdint.h>
void fake_reply(const char *text);
void fake_reply_bytes(const unsigned char *bytes, size_t size);
void fake_write_error(void);
void fake_flush_error(void);
const char *fake_command(void);
unsigned fake_write_count(void);
unsigned fake_close_count(void);
size_t fake_command_size(void);
const unsigned char *fake_settings(void);
void fake_clock_counter(uint32_t value);
void fake_program_reply(const char *reply);
#endif
