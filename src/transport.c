#include "internal.h"

#include <stdio.h>
#include <string.h>

uint64_t mcfluke_now_ms(void)
{
    LARGE_INTEGER counter, frequency;
    uint64_t ticks, rate;
    (void)QueryPerformanceCounter(&counter);
    (void)QueryPerformanceFrequency(&frequency);
    ticks = (uint64_t)counter.QuadPart;
    rate = (uint64_t)frequency.QuadPart;
    return (ticks / rate) * 1000 + ((ticks % rate) * 1000) / rate;
}

static mcfluke_status os_error(mcfluke_error *error, const char *operation)
{
    DWORD code = GetLastError();
    WCHAR wide_detail[160] = {0};
    char detail[160] = {0}, message[256];
    if (FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        NULL, code, 0, wide_detail, (DWORD)(sizeof(wide_detail) / sizeof(wide_detail[0])), NULL)) {
        if (!WideCharToMultiByte(CP_UTF8, 0, wide_detail, -1, detail, (int)sizeof(detail), NULL, NULL))
            detail[0] = '\0';
    }
    (void)snprintf(message, sizeof(message), "%s (Windows %lu): %s",
        operation, (unsigned long)code, detail);
    return mcfluke_fail(error, MCFLUKE_IO_ERROR, (int32_t)code, message);
}

mcfluke_status mcfluke_transport_open(mcfluke_transport *transport,
    const char *port, mcfluke_error *error)
{
    memset(transport, 0, sizeof(*transport));
    if (strcmp(port, "sim://187") == 0) {
        transport->simulated = 1;
        transport->sim_clock_started = mcfluke_now_ms();
        return MCFLUKE_OK;
    }
    {
        char path[256];
        DCB config;
        HANDLE handle;
        const char *number = port;
        if (strncmp(number, "\\\\.\\", 4) == 0) number += 4;
        if (strlen(number) < 4 || !((number[0] == 'C' || number[0] == 'c') &&
              (number[1] == 'O' || number[1] == 'o') &&
              (number[2] == 'M' || number[2] == 'm')))
            return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "Windows ports must use COM<number>.");
        number += 3;
        if (*number < '1' || *number > '9')
            return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "COM port number must be positive.");
        {
            const char *p;
            for (p = number; *p; ++p)
                if (*p < '0' || *p > '9')
                    return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "Invalid COM port name.");
        }
        if (strlen(number) > 32)
            return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "COM port name is too long.");
        (void)snprintf(path, sizeof(path), "\\\\.\\COM%s", number);
        handle = CreateFileA(path, GENERIC_READ | GENERIC_WRITE, 0, NULL,
            OPEN_EXISTING, 0, NULL);
        if (handle == INVALID_HANDLE_VALUE) return os_error(error, "Cannot open serial port");
        memset(&config, 0, sizeof(config));
        config.DCBlength = (DWORD)sizeof(config);
        if (!GetCommState(handle, &config)) {
            mcfluke_status status = os_error(error, "Cannot read serial configuration");
            CloseHandle(handle);
            return status;
        }
        config.BaudRate = CBR_9600;
        config.ByteSize = 8;
        config.Parity = NOPARITY;
        config.StopBits = ONESTOPBIT;
        config.fBinary = TRUE;
        config.fParity = FALSE;
        config.fOutxCtsFlow = FALSE;
        config.fOutxDsrFlow = FALSE;
        config.fDtrControl = DTR_CONTROL_DISABLE;
        config.fDsrSensitivity = FALSE;
        config.fOutX = FALSE;
        config.fInX = FALSE;
        config.fErrorChar = FALSE;
        config.fNull = FALSE;
        config.fRtsControl = RTS_CONTROL_DISABLE;
        config.fAbortOnError = FALSE;
        if (!SetCommState(handle, &config)) {
            mcfluke_status status = os_error(error, "Cannot configure 9600 8N1");
            CloseHandle(handle);
            return status;
        }
        transport->handle = handle;
    }
    return MCFLUKE_OK;
}

void mcfluke_transport_close(mcfluke_transport *transport)
{
    if (transport->simulated) return;
    if (transport->handle) CloseHandle(transport->handle);
    transport->handle = NULL;
}

mcfluke_status mcfluke_transport_flush(mcfluke_transport *transport, mcfluke_error *error)
{
    if (transport->simulated) {
        transport->pending_size = transport->pending_position = 0;
        return MCFLUKE_OK;
    }
    if (!PurgeComm(transport->handle, PURGE_RXCLEAR | PURGE_TXCLEAR))
        return os_error(error, "Cannot clear serial buffers");
    return MCFLUKE_OK;
}

static mcfluke_status simulate_write(mcfluke_transport *transport,
    const char *data, size_t size, mcfluke_error *error)
{
    char command[32];
    const char *reply = "1\r";
    if (size == 33 && memcmp(data, "PS ", 3) == 0 && data[32] == '\r') {
        const unsigned char *body = (const unsigned char *)data + 3;
        memcpy(transport->sim_settings, body, 29);
        transport->sim_clock_base = (uint32_t)body[12] | ((uint32_t)body[13] << 8) |
            ((uint32_t)body[14] << 16) | ((uint32_t)body[15] << 24);
        transport->sim_clock_started = mcfluke_now_ms();
        memcpy(transport->pending, "0\r", 2);
        transport->pending_size = 2;
        transport->pending_position = 0;
        return MCFLUKE_OK;
    }
    if (size >= sizeof(command))
        return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "Simulator command is too long.");
    memcpy(command, data, size);
    command[size] = '\0';
    if (strcmp(command, "ID\r") == 0) {
        reply = "0\rFLUKE 187,SIM-1.0,SIMULATED\r";
    } else if (strcmp(command, "QS\r") == 0) {
        uint32_t ticks = transport->sim_clock_base +
            (uint32_t)((mcfluke_now_ms() - transport->sim_clock_started) / 100);
        size_t i;
        memcpy(transport->pending, "0\rQS,", 5);
        memcpy(transport->pending + 5, transport->sim_settings, 29);
        for (i = 0; i < 4; ++i) transport->pending[5 + 12 + i] = (char)(ticks >> (8 * i));
        transport->pending_size = 34;
        transport->pending_position = 0;
        return MCFLUKE_OK;
    } else if (strcmp(command, "QM\r") == 0) {
        if (transport->sim_range)
            reply = transport->sim_ac ? "0\rQM,+1234.0 mV AC\r" : "0\rQM,+1234.0 mV DC\r";
        else
            reply = transport->sim_ac ? "0\rQM,+1.234 V AC\r" : "0\rQM,+1.234 V DC\r";
    } else if (strcmp(command, "DS\r") == 0 || strcmp(command, "RI\r") == 0) {
        transport->sim_ac = transport->sim_range = 0;
        if (strcmp(command, "RI\r") == 0) {
            transport->sim_clock_base = 0;
            transport->sim_clock_started = mcfluke_now_ms();
        }
        reply = "0\r";
    } else if (size == 6 && strncmp(command, "SF ", 3) == 0 && command[5] == '\r') {
        int key = (command[3] - '0') * 10 + command[4] - '0';
        if ((key >= 10 && key <= 22) || (key >= 27 && key <= 29)) {
            if (key == 10) transport->sim_ac = !transport->sim_ac;
            if (key == 17) transport->sim_range = !transport->sim_range;
            if (key == 27) transport->sim_ac = transport->sim_range = 0;
            reply = "0\r";
        }
    }
    (void)snprintf(transport->pending, sizeof(transport->pending), "%s", reply);
    transport->pending_size = strlen(transport->pending);
    transport->pending_position = 0;
    return MCFLUKE_OK;
}

mcfluke_status mcfluke_transport_write(mcfluke_transport *transport,
    const char *data, size_t size, uint32_t timeout_ms, mcfluke_error *error)
{
    if (!timeout_ms)
        return mcfluke_fail(error, MCFLUKE_TIMEOUT, 0, "Command deadline expired before writing.");
    if (transport->simulated) return simulate_write(transport, data, size, error);
    {
        COMMTIMEOUTS timeouts = {MAXDWORD, 0, timeout_ms, 0, timeout_ms};
        DWORD written = 0;
        if (!SetCommTimeouts(transport->handle, &timeouts))
            return os_error(error, "Cannot set serial timeout");
        if (!WriteFile(transport->handle, data, (DWORD)size, &written, NULL))
            return os_error(error, "Cannot write serial command");
        if (written != size)
            return mcfluke_fail(error, MCFLUKE_IO_ERROR, 0, "Incomplete serial write; command delivery is uncertain.");
    }
    return MCFLUKE_OK;
}

mcfluke_status mcfluke_transport_read_byte(mcfluke_transport *transport,
    unsigned char *value, uint32_t timeout_ms, mcfluke_error *error)
{
    if (transport->simulated) {
        if (transport->pending_position >= transport->pending_size)
            return mcfluke_fail(error, MCFLUKE_TIMEOUT, 0, "Simulator reply is exhausted.");
        *value = (unsigned char)transport->pending[transport->pending_position++];
        return MCFLUKE_OK;
    }
    {
        COMMTIMEOUTS timeouts = {MAXDWORD, 0, timeout_ms, 0, timeout_ms};
        DWORD count = 0;
        if (!SetCommTimeouts(transport->handle, &timeouts))
            return os_error(error, "Cannot set serial timeout");
        if (!ReadFile(transport->handle, value, 1, &count, NULL))
            return os_error(error, "Cannot read serial reply");
        if (count != 1)
            return mcfluke_fail(error, MCFLUKE_TIMEOUT, 0,
                "No serial reply before timeout. A control command may already have executed.");
    }
    return MCFLUKE_OK;
}
