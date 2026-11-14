#if !defined(_WIN32)
#define _POSIX_C_SOURCE 200809L
#endif
#include "internal.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>
#endif

#if defined(_WIN32)
uint64_t mcfluke_now_ms(void) { return (uint64_t)GetTickCount64(); }

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
#else
uint64_t mcfluke_now_ms(void)
{
    struct timespec time;
    (void)clock_gettime(CLOCK_MONOTONIC, &time);
    return (uint64_t)time.tv_sec * 1000 + (uint64_t)time.tv_nsec / 1000000;
}

static mcfluke_status os_error(mcfluke_error *error, const char *operation)
{
    int code = errno;
    char message[256];
    (void)snprintf(message, sizeof(message), "%s (errno %d): %s", operation, code, strerror(code));
    return mcfluke_fail(error, MCFLUKE_IO_ERROR, code, message);
}
#endif

mcfluke_status mcfluke_transport_open(mcfluke_transport *transport,
    const char *port, mcfluke_error *error)
{
    memset(transport, 0, sizeof(*transport));
    transport->fd = -1;
    if (strcmp(port, "sim://187") == 0) {
        transport->simulated = 1;
        return MCFLUKE_OK;
    }
#if defined(_WIN32)
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
#else
    {
        struct termios config;
        int fd = open(port, O_RDWR | O_NOCTTY | O_NONBLOCK);
        if (fd < 0) return os_error(error, "Cannot open serial port");
        if (tcgetattr(fd, &config) != 0) {
            mcfluke_status status = os_error(error, "Cannot read serial configuration");
            close(fd);
            return status;
        }
        config.c_iflag &= ~(IGNBRK | BRKINT | PARMRK | ISTRIP | INLCR | IGNCR | ICRNL | IXON | IXOFF | IXANY);
        config.c_oflag &= ~OPOST;
        config.c_lflag &= ~(ECHO | ECHONL | ICANON | ISIG | IEXTEN);
        config.c_cflag &= ~(CSIZE | PARENB | CSTOPB);
#ifdef CRTSCTS
        config.c_cflag &= ~CRTSCTS;
#endif
        config.c_cflag |= CS8 | CLOCAL | CREAD;
        config.c_cc[VMIN] = 0;
        config.c_cc[VTIME] = 0;
        if (cfsetispeed(&config, B9600) != 0 || cfsetospeed(&config, B9600) != 0 ||
            tcsetattr(fd, TCSANOW, &config) != 0) {
            mcfluke_status status = os_error(error, "Cannot configure 9600 8N1");
            close(fd);
            return status;
        }
        transport->fd = fd;
    }
#endif
    return MCFLUKE_OK;
}

void mcfluke_transport_close(mcfluke_transport *transport)
{
    if (transport->simulated) return;
#if defined(_WIN32)
    if (transport->handle) CloseHandle((HANDLE)transport->handle);
    transport->handle = NULL;
#else
    if (transport->fd >= 0) close(transport->fd);
    transport->fd = -1;
#endif
}

mcfluke_status mcfluke_transport_flush(mcfluke_transport *transport, mcfluke_error *error)
{
    if (transport->simulated) {
        transport->pending_size = transport->pending_position = 0;
        return MCFLUKE_OK;
    }
#if defined(_WIN32)
    if (!PurgeComm((HANDLE)transport->handle, PURGE_RXCLEAR | PURGE_TXCLEAR))
        return os_error(error, "Cannot clear serial buffers");
#else
    if (tcflush(transport->fd, TCIOFLUSH) != 0)
        return os_error(error, "Cannot clear serial buffers");
#endif
    return MCFLUKE_OK;
}

static mcfluke_status simulate_write(mcfluke_transport *transport,
    const char *data, size_t size, mcfluke_error *error)
{
    char command[32];
    const char *reply = "1\r";
    if (size >= sizeof(command))
        return mcfluke_fail(error, MCFLUKE_INVALID_ARGUMENT, 0, "Simulator command is too long.");
    memcpy(command, data, size);
    command[size] = '\0';
    if (strcmp(command, "ID\r") == 0) {
        reply = "0\rFLUKE 187,SIM-1.0,SIMULATED\r";
    } else if (strcmp(command, "QM\r") == 0) {
        if (transport->sim_range)
            reply = transport->sim_ac ? "0\rQM,+1234.0 mV AC\r" : "0\rQM,+1234.0 mV DC\r";
        else
            reply = transport->sim_ac ? "0\rQM,+1.234 V AC\r" : "0\rQM,+1.234 V DC\r";
    } else if (strcmp(command, "DS\r") == 0 || strcmp(command, "RI\r") == 0) {
        transport->sim_ac = transport->sim_range = 0;
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

#if !defined(_WIN32)
static mcfluke_status wait_ready(int fd, short events, uint32_t timeout_ms, mcfluke_error *error)
{
    uint64_t deadline = mcfluke_now_ms() + timeout_ms;
    for (;;) {
        struct pollfd descriptor = {fd, events, 0};
        uint64_t now = mcfluke_now_ms();
        int result = poll(&descriptor, 1, deadline > now ? (int)(deadline - now) : 0);
        if (result < 0 && errno == EINTR) continue;
        if (result < 0) return os_error(error, "Serial poll failed");
        if (result == 0)
            return mcfluke_fail(error, MCFLUKE_TIMEOUT, 0,
                "Serial response timed out. A control command may already have executed.");
        if (descriptor.revents & (POLLERR | POLLHUP | POLLNVAL))
            return mcfluke_fail(error, MCFLUKE_IO_ERROR, 0, "Serial device disconnected.");
        if (descriptor.revents & events) return MCFLUKE_OK;
    }
}
#endif

mcfluke_status mcfluke_transport_write(mcfluke_transport *transport,
    const char *data, size_t size, uint32_t timeout_ms, mcfluke_error *error)
{
    if (transport->simulated) return simulate_write(transport, data, size, error);
    if (!timeout_ms)
        return mcfluke_fail(error, MCFLUKE_TIMEOUT, 0, "Command deadline expired before writing.");
#if defined(_WIN32)
    {
        COMMTIMEOUTS timeouts = {MAXDWORD, 0, timeout_ms, 0, timeout_ms};
        DWORD written = 0;
        if (!SetCommTimeouts((HANDLE)transport->handle, &timeouts))
            return os_error(error, "Cannot set serial timeout");
        if (!WriteFile((HANDLE)transport->handle, data, (DWORD)size, &written, NULL))
            return os_error(error, "Cannot write serial command");
        if (written != size)
            return mcfluke_fail(error, MCFLUKE_IO_ERROR, 0, "Incomplete serial write; command delivery is uncertain.");
    }
#else
    {
        size_t position = 0;
        uint64_t deadline = mcfluke_now_ms() + timeout_ms;
        while (position < size) {
            uint64_t now = mcfluke_now_ms();
            mcfluke_status status = wait_ready(transport->fd, POLLOUT,
                deadline > now ? (uint32_t)(deadline - now) : 0, error);
            ssize_t count;
            if (status != MCFLUKE_OK) return status;
            count = write(transport->fd, data + position, size - position);
            if (count < 0 && (errno == EINTR || errno == EAGAIN)) continue;
            if (count < 0) return os_error(error, "Cannot write serial command");
            if (count == 0) return mcfluke_fail(error, MCFLUKE_IO_ERROR, 0, "Serial write returned zero bytes.");
            position += (size_t)count;
        }
    }
#endif
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
#if defined(_WIN32)
    {
        COMMTIMEOUTS timeouts = {MAXDWORD, 0, timeout_ms, 0, timeout_ms};
        DWORD count = 0;
        if (!SetCommTimeouts((HANDLE)transport->handle, &timeouts))
            return os_error(error, "Cannot set serial timeout");
        if (!ReadFile((HANDLE)transport->handle, value, 1, &count, NULL))
            return os_error(error, "Cannot read serial reply");
        if (count != 1)
            return mcfluke_fail(error, MCFLUKE_TIMEOUT, 0,
                "No serial reply before timeout. A control command may already have executed.");
    }
#else
    {
        uint64_t deadline = mcfluke_now_ms() + timeout_ms;
        for (;;) {
            uint64_t now = mcfluke_now_ms();
            mcfluke_status status = wait_ready(transport->fd, POLLIN,
                deadline > now ? (uint32_t)(deadline - now) : 0, error);
            ssize_t count;
            if (status != MCFLUKE_OK) return status;
            count = read(transport->fd, value, 1);
            if (count < 0 && (errno == EINTR || errno == EAGAIN)) continue;
            if (count < 0) return os_error(error, "Cannot read serial reply");
            if (count == 0)
                return mcfluke_fail(error, MCFLUKE_IO_ERROR, 0, "Serial device returned end of stream.");
            break;
        }
    }
#endif
    return MCFLUKE_OK;
}
