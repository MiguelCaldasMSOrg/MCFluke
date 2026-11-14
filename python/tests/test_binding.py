import contextlib
import ctypes as C
import gc
import io
from pathlib import Path
import subprocess
import sys
import threading
import unittest
from unittest.mock import patch
import weakref

import mcfluke as m
from mcfluke import _native
from mcfluke.__main__ import main


class Function:
    def __init__(self, callback):
        self.callback = callback

    def __call__(self, *arguments):
        return self.callback(*arguments)


class FakeDLL:
    """An ABI-call fixture, not a simulator or serial implementation."""

    def __init__(self):
        self.calls = []
        self.timeout = 1500
        self.open_status = 0
        self.display_status = 0
        self.error_message = b"fixture diagnostic"
        self.read_entered = threading.Event()
        self.read_release = None
        self.handle = C.pointer(_native.DeviceHandle())
        functions = {
            "version": lambda: b"fixture-1",
            "abi_version": lambda: 1,
            "status_message": lambda status: b"fixture status",
            "open": self.open,
            "close": self.close,
            "get_identity": self.get_identity,
            "read_display": self.read_display,
            "get_timeout": self.get_timeout,
            "set_timeout": self.set_timeout,
            "press_button": self.press_button,
            "default_setup": self.default_setup,
            "reset_instrument": self.reset_instrument,
            "parse_identity": lambda data, output, error: self.get_identity(None, output, error),
            "parse_display": lambda data, output, error: self.read_display(None, output, error),
        }
        for name, callback in functions.items():
            setattr(self, f"mcfluke_{name}", Function(callback))

    def failure(self, status, output):
        error = C.cast(output, C.POINTER(_native.Error)).contents
        error.status = status
        error.os_code = 1460
        error.message = self.error_message
        return status

    def open(self, port, timeout, output, error):
        self.calls.append(("open", port, timeout))
        if self.open_status:
            return self.failure(self.open_status, error)
        C.cast(output, C.POINTER(_native.Handle))[0] = self.handle
        self.timeout = timeout
        return 0

    def close(self, handle):
        self.calls.append(("close",))

    def get_identity(self, handle, output, error):
        self.calls.append(("id",))
        result = C.cast(output, C.POINTER(_native.Identity)).contents
        result.model, result.firmware, result.serial_number = b"FLUKE 187", b"SIM-1.0", b"SIMULATED"
        return 0

    def read_display(self, handle, output, error):
        self.calls.append(("display",))
        self.read_entered.set()
        if self.read_release is not None and not self.read_release.wait(3):
            raise AssertionError("test failed to release blocked read")
        if self.display_status:
            return self.failure(self.display_status, error)
        result = C.cast(output, C.POINTER(_native.Reading)).contents
        result.value, result.state = 1.234, 0
        result.display, result.unit = b"+1.234 V DC", b"V DC"
        return 0

    def get_timeout(self, handle, output, error):
        self.calls.append(("get-timeout",))
        C.cast(output, C.POINTER(C.c_uint32)).contents.value = self.timeout
        return 0

    def set_timeout(self, handle, timeout, error):
        self.calls.append(("set-timeout", timeout))
        self.timeout = timeout
        return 0

    def press_button(self, handle, button, confirmation, error):
        self.calls.append(("button", button, confirmation))
        return 0

    def default_setup(self, handle, error):
        self.calls.append(("defaults",))
        return 0

    def reset_instrument(self, handle, confirmation, error):
        self.calls.append(("reset", confirmation))
        return 0


class ABITests(unittest.TestCase):
    def test_fixed_layout_and_cdecl_signatures(self):
        self.assertEqual(C.sizeof(_native.Error), 264)
        self.assertEqual(C.sizeof(_native.Identity), 128)
        self.assertEqual(C.sizeof(_native.Reading), 176)
        self.assertEqual(_native.Reading.value.offset, 0)
        self.assertEqual(_native.Reading.state.offset, 8)
        self.assertEqual(_native.Reading.display.offset, 12)
        self.assertEqual(_native.Reading.unit.offset, 140)
        library = _native.Library(FakeDLL())
        self.assertEqual(library.mcfluke_open.argtypes, [
            C.c_char_p, C.c_uint32, C.POINTER(_native.Handle), C.POINTER(_native.Error)
        ])
        self.assertEqual(library.mcfluke_get_timeout.argtypes, [
            _native.Handle, C.POINTER(C.c_uint32), C.POINTER(_native.Error)
        ])
        self.assertIs(library.mcfluke_read_display.restype, C.c_int32)
        self.assertEqual(library.mcfluke_read_display.argtypes, [
            _native.Handle, C.POINTER(_native.Reading), C.POINTER(_native.Error)
        ])
        self.assertEqual(library.mcfluke_parse_identity.argtypes, [
            C.c_char_p, C.POINTER(_native.Identity), C.POINTER(_native.Error)
        ])
        self.assertIsNone(library.mcfluke_close.restype)
        self.assertIs(library.mcfluke_version.restype, C.c_char_p)

    def test_abi_mismatch_and_missing_symbols_are_clear(self):
        dll = FakeDLL()
        dll.mcfluke_abi_version = Function(lambda: 99)
        with self.assertRaisesRegex(m.NativeABIError, "ABI 99"):
            _native.Library(dll)
        del dll.mcfluke_read_display
        with self.assertRaisesRegex(m.NativeABIError, "missing an ABI symbol"):
            _native.Library(dll)

    def test_import_does_not_load_library(self):
        script = (
            "import ctypes\n"
            "import sys\n"
            "def fail(*args, **kwargs): raise AssertionError('loaded on import')\n"
            "ctypes.CDLL = fail\n"
            "import mcfluke\n"
            "assert 'ctypes.util' not in sys.modules, 'ctypes.util imported eagerly'\n"
        )
        result = subprocess.run(
            [sys.executable, "-c", script], capture_output=True, text=True, timeout=10
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_explicit_missing_or_relative_library_does_not_fall_back(self):
        with patch.object(_native.C, "CDLL") as loader:
            for candidate in ("relative.dll", Path(__file__).parent / "missing-native-fixture.dll"):
                with self.subTest(candidate=candidate):
                    with self.assertRaises(m.DllNotFoundError):
                        _native.load(candidate)
            loader.assert_not_called()

    def test_explicit_load_uses_cdll_not_windll(self):
        dll = FakeDLL()
        with patch.object(_native.C, "CDLL", return_value=dll) as loader:
            with patch.dict(_native._libraries, clear=True):
                # This known existing file is only a path fixture; CDLL is mocked.
                library = _native.load(Path(__file__).resolve())
                self.assertIs(library.dll, dll)
                loader.assert_called_once_with(str(Path(__file__).resolve()))
                self.assertIs(_native.load(Path(__file__).resolve()), library)


class BindingTests(unittest.TestCase):
    def setUp(self):
        self.dll = FakeDLL()
        self.library = _native.Library(self.dll)
        self.loader = patch.object(_native, "load", return_value=self.library)
        self.loader.start()
        self.addCleanup(self.loader.stop)

    def test_public_operations_and_explicit_confirmation(self):
        with m.MCFluke("sim://187") as meter:
            self.assertFalse(meter.closed)
            self.assertEqual(meter.get_identity(), m.Identity("FLUKE 187", "SIM-1.0", "SIMULATED"))
            self.assertEqual(meter.read_display().value, 1.234)
            self.assertEqual(meter.get_timeout(), 1500)
            meter.set_timeout(50)
            self.assertEqual(meter.get_timeout(), 50)
            meter.press_button(m.Button.RANGE)
            meter.press_button(m.Button.CALIBRATION, confirm_calibration=True)
            meter.default_setup()
            meter.reset_instrument()
            meter.reset_instrument(confirmation=True)
        self.assertIn(("button", 17, 0), self.dll.calls)
        self.assertIn(("button", 20, 1), self.dll.calls)
        self.assertIn(("reset", 0), self.dll.calls)
        self.assertIn(("reset", 1), self.dll.calls)
        self.assertTrue(meter.closed)
        self.assertEqual(m.get_version(), m.Version("fixture-1", 1))
        self.assertEqual(m.status_message(m.Status.TIMEOUT), "fixture status")
        self.assertEqual(m.parse_identity("payload").model, "FLUKE 187")
        self.assertEqual(m.parse_display("payload").display, "+1.234 V DC")

    def test_non_numeric_readings_have_no_value(self):
        for state in (m.ReadingState.OUT_OF_RANGE, m.ReadingState.TEXT):
            with self.subTest(state=state):
                result = _native.Reading(value=0.0, state=state, display=b"OL", unit=b"V DC")
                reading = m._reading(result)
                self.assertIsNone(reading.value)
                self.assertEqual(reading.display, "OL")
                self.assertEqual(reading.unit, "V DC")
        with self.assertRaises(m.ProtocolError):
            m._reading(_native.Reading(state=999))

    def test_structured_timeout_and_other_errors(self):
        with m.MCFluke("sim://187") as meter:
            for status, exception in (
                (3, m.DeviceTimeoutError), (2, m.DeviceIOError),
                (4, m.ProtocolError), (5, m.CommandRejectedError),
                (7, m.WrongModelError), (8, m.ConfirmationRequiredError),
                (9, m.UnsupportedOperationError), (10, m.MCFlukeError),
            ):
                with self.subTest(status=status):
                    self.dll.display_status = status
                    with self.assertRaises(exception) as caught:
                        meter.read_display()
                    self.assertEqual(caught.exception.status, status)
                    self.assertEqual(caught.exception.os_code, 1460)
                    self.assertEqual(caught.exception.operation, "read_display")
                    self.assertIn("fixture diagnostic", str(caught.exception))
            self.dll.display_status = 0
            self.assertEqual(meter.read_display().value, 1.234)
        self.assertTrue(issubclass(m.DeviceTimeoutError, TimeoutError))

    def test_error_falls_back_to_native_status_message(self):
        self.dll.error_message = b""
        self.dll.open_status = 3
        with self.assertRaisesRegex(m.DeviceTimeoutError, "fixture status"):
            m.MCFluke("sim://187")
        self.assertNotIn(("close",), self.dll.calls)

    def test_invalid_values_cannot_wrap_ctypes_integers_or_truncate_strings(self):
        for timeout in (0, 49, 60001, 2**32 + 1500, True, 1.5, "1500"):
            with self.subTest(timeout=timeout):
                with self.assertRaises(m.InvalidArgumentError):
                    m.MCFluke("sim://187", timeout=timeout)
        for port in (None, "sim://187\0COM3"):
            with self.assertRaises(m.InvalidArgumentError):
                m.MCFluke(port)
        with m.MCFluke("sim://187") as meter:
            for value in (1, "true", None):
                with self.assertRaises(m.InvalidArgumentError):
                    meter.reset_instrument(confirmation=value)
            with self.assertRaises(m.InvalidArgumentError):
                meter.press_button(2**32 + 17)
            with self.assertRaises(m.InvalidArgumentError):
                meter.set_timeout(False)
        with self.assertRaises(m.InvalidArgumentError):
            m.parse_identity("bad\0payload")

    def test_close_is_idempotent_and_operations_guard_disposal(self):
        meter = m.MCFluke("sim://187")
        meter.close()
        meter.close()
        operations = (
            meter.get_identity, meter.read_display, meter.get_timeout,
            lambda: meter.set_timeout(1500), lambda: meter.press_button(m.Button.RANGE),
            meter.default_setup, meter.reset_instrument, meter.__enter__,
        )
        for operation in operations:
            with self.assertRaises(m.DeviceClosedError):
                operation()
        self.assertEqual(self.dll.calls.count(("close",)), 1)

    def test_context_disposes_during_exception(self):
        with self.assertRaisesRegex(ValueError, "test failure"):
            with m.MCFluke("sim://187") as meter:
                raise ValueError("test failure")
        self.assertTrue(meter.closed)

    def test_finalizer_does_not_retain_instance(self):
        meter = m.MCFluke("sim://187")
        reference = weakref.ref(meter)
        del meter
        gc.collect()
        self.assertIsNone(reference())
        self.assertEqual(self.dll.calls.count(("close",)), 1)

    def test_close_and_reads_are_serialized(self):
        self.dll.read_release = threading.Event()
        meter = m.MCFluke("sim://187")
        self.addCleanup(meter.close)
        failures = []

        def read():
            try:
                meter.read_display()
            except BaseException as exc:
                failures.append(exc)

        reader = threading.Thread(target=read)
        closer = threading.Thread(target=meter.close)
        reader.start()
        self.assertTrue(self.dll.read_entered.wait(1))
        closer.start()
        try:
            self.assertNotIn(("close",), self.dll.calls)
        finally:
            self.dll.read_release.set()
            reader.join(3)
            closer.join(3)
        self.assertFalse(reader.is_alive())
        self.assertFalse(closer.is_alive())
        self.assertEqual(failures, [])
        self.assertEqual(self.dll.calls[-1], ("close",))

    def test_example_only_identifies_and_reads_with_two_second_default(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output), patch("mcfluke.__main__.time.sleep") as sleep:
            self.assertEqual(main(["--port", "sim://187", "--count", "2"]), 0)
        self.assertEqual([call[0] for call in self.dll.calls], ["open", "id", "display", "display", "close"])
        sleep.assert_called_once_with(2.0)
        self.assertEqual(len(output.getvalue().splitlines()), 3)

    def test_example_reports_timeout_and_disposes(self):
        self.dll.display_status = 3
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()) as stderr:
            self.assertEqual(main(["--port", "sim://187", "--count", "1"]), 1)
        self.assertIn("fixture diagnostic", stderr.getvalue())
        self.assertEqual(self.dll.calls[-1], ("close",))

    def test_example_ctrl_c_disposes(self):
        with patch.object(self.library, "mcfluke_read_display", side_effect=KeyboardInterrupt):
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(main(["--port", "sim://187", "--count", "1"]), 130)
        self.assertEqual(self.dll.calls[-1], ("close",))


if __name__ == "__main__":
    unittest.main()
