"""POSIX pseudo-terminal tests use the real serial backend and fragmented wire replies."""

import ctypes
import os
from pathlib import Path
import select
import sys
import threading
import time
import unittest

LIBRARY = Path(sys.argv.pop(1)).resolve()
if os.name != "nt":
    import pty


class Error(ctypes.Structure):
    _fields_ = [
        ("status", ctypes.c_int32),
        ("os_code", ctypes.c_int32),
        ("message", ctypes.c_char * 256),
    ]


class Reading(ctypes.Structure):
    _fields_ = [
        ("value", ctypes.c_double),
        ("state", ctypes.c_int32),
        ("display", ctypes.c_char * 128),
        ("unit", ctypes.c_char * 32),
    ]


@unittest.skipIf(os.name == "nt", "POSIX pseudo-terminals are unavailable on Windows; use the live read-only check.")
class SerialTests(unittest.TestCase):
    def setUp(self):
        self.lib = ctypes.CDLL(str(LIBRARY))
        self.lib.mcfluke_open.argtypes = [
            ctypes.c_char_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(Error)
        ]
        self.lib.mcfluke_open.restype = ctypes.c_int
        self.lib.mcfluke_close.argtypes = [ctypes.c_void_p]
        self.lib.mcfluke_close.restype = None
        self.lib.mcfluke_read_display.argtypes = [
            ctypes.c_void_p, ctypes.POINTER(Reading), ctypes.POINTER(Error)
        ]
        self.lib.mcfluke_read_display.restype = ctypes.c_int
        self.lib.mcfluke_set_timeout.argtypes = [
            ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(Error)
        ]
        self.lib.mcfluke_set_timeout.restype = ctypes.c_int
        self.master, self.slave = pty.openpty()
        self.port = os.ttyname(self.slave).encode()
        self.stop = threading.Event()
        self.reply = b"0\rQM,+1.234 V DC\r"
        self.identity = b"0\rFLUKE 187,V2.02,PTY-TEST\r"
        self.delay = 0.001
        self.chunk_size = 4
        self.commands = []
        self.thread = threading.Thread(target=self.emulate, daemon=True)
        self.thread.start()
        self.device = ctypes.c_void_p()
        self.error = Error()

    def tearDown(self):
        if self.device:
            self.lib.mcfluke_close(self.device)
        self.stop.set()
        self.thread.join(timeout=2)
        os.close(self.master)
        os.close(self.slave)

    def emulate(self):
        pending = b""
        while not self.stop.is_set():
            if not select.select([self.master], [], [], 0.05)[0]:
                continue
            pending += os.read(self.master, 256)
            while b"\r" in pending:
                command, pending = pending.split(b"\r", 1)
                self.commands.append(command)
                response = self.identity if command == b"ID" else self.reply
                for position in range(0, len(response), self.chunk_size):
                    if self.stop.wait(self.delay):
                        return
                    os.write(self.master, response[position:position + self.chunk_size])

    def connect(self, timeout=1000):
        status = self.lib.mcfluke_open(self.port, timeout, ctypes.byref(self.device), ctypes.byref(self.error))
        self.assertEqual(status, 0, self.error.message.decode())

    def set_timeout(self, milliseconds):
        self.assertEqual(
            self.lib.mcfluke_set_timeout(self.device, milliseconds, ctypes.byref(self.error)),
            0, self.error.message.decode(),
        )

    def test_fragmented_real_backend(self):
        self.connect()
        reading = Reading()
        self.assertEqual(self.lib.mcfluke_read_display(self.device, ctypes.byref(reading), ctypes.byref(self.error)), 0)
        self.assertEqual(reading.value, 1.234)
        self.assertEqual(reading.unit, b"V DC")
        self.assertEqual(self.commands, [b"ID", b"QM"])

    def test_partial_timeout_and_recovery(self):
        self.connect()
        self.set_timeout(150)
        self.reply = b"0\rQM,+1"
        reading = Reading()
        start = time.monotonic()
        self.assertEqual(self.lib.mcfluke_read_display(self.device, ctypes.byref(reading), ctypes.byref(self.error)), 3)
        self.assertLess(time.monotonic() - start, 0.5)
        self.reply = b"0\rQM,-2.5 mV AC\r"
        self.assertEqual(self.lib.mcfluke_read_display(self.device, ctypes.byref(reading), ctypes.byref(self.error)), 0)
        self.assertEqual(reading.value, -2.5)

    def test_rejection_and_non_ascii(self):
        self.connect()
        reading = Reading()
        self.reply = b"1\r"
        self.assertEqual(self.lib.mcfluke_read_display(self.device, ctypes.byref(reading), ctypes.byref(self.error)), 5)
        self.reply = b"0\rQM,\xff\r"
        self.assertEqual(self.lib.mcfluke_read_display(self.device, ctypes.byref(reading), ctypes.byref(self.error)), 4)

    def test_wrong_model(self):
        self.identity = b"0\rFLUKE 189,V1,PTY-TEST\r"
        status = self.lib.mcfluke_open(self.port, 1000, ctypes.byref(self.device), ctypes.byref(self.error))
        self.assertEqual(status, 7)
        self.assertFalse(self.device)

    def test_total_deadline_not_per_byte(self):
        self.connect()
        self.set_timeout(150)
        self.chunk_size = 1
        self.delay = 0.05
        reading = Reading()
        start = time.monotonic()
        self.assertEqual(self.lib.mcfluke_read_display(self.device, ctypes.byref(reading), ctypes.byref(self.error)), 3)
        self.assertLess(time.monotonic() - start, 0.4)


if __name__ == "__main__":
    unittest.main()
