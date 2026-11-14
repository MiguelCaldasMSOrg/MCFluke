"""Lazy standard-library bindings to the MCFluke C core (not a serial protocol)."""

import ctypes as C
from dataclasses import dataclass
from enum import IntEnum
import operator
import threading
import weakref

from . import _native
from ._native import DllNotFoundError, NativeABIError


CLOCK_TICKS_PER_SECOND = 10
CLOCK_TICKS_PER_DAY = 864000


class Status(IntEnum):
    OK = 0
    INVALID_ARGUMENT = 1
    IO_ERROR = 2
    TIMEOUT = 3
    PROTOCOL_ERROR = 4
    COMMAND_REJECTED = 5
    BUFFER_TOO_SMALL = 6
    WRONG_MODEL = 7
    CONFIRMATION_REQUIRED = 8
    UNSUPPORTED = 9
    OUT_OF_MEMORY = 10


class Button(IntEnum):
    BLUE = 10
    HOLD = 11
    MIN_MAX = 12
    REL = 13
    UP = 14
    SHIFT = 15
    HZ = 16
    RANGE = 17
    DOWN = 18
    BACKLIGHT = 19
    CALIBRATION = 20
    AUTO_HOLD = 21
    FAST_MIN_MAX = 22
    LOGGING = 23
    CANCEL = 27
    WAKEUP = 28
    SETUP = 29
    SAVE = 30


class ReadingState(IntEnum):
    VALUE = 0
    OUT_OF_RANGE = 1
    TEXT = 2


@dataclass(frozen=True)
class Identity:
    model: str
    firmware: str
    serial_number: str


@dataclass(frozen=True)
class Reading:
    value: float | None
    state: ReadingState
    display: str
    unit: str


@dataclass(frozen=True)
class Version:
    version: str
    abi: int


class MCFlukeError(RuntimeError):
    def __init__(self, message, *, status, os_code=0, operation=None):
        self.status = status
        self.os_code = os_code
        self.operation = operation
        prefix = f"{operation}: " if operation else ""
        suffix = f" [status={status}, OS={os_code}]" if os_code else f" [status={status}]"
        super().__init__(f"{prefix}{message}{suffix}")


class InvalidArgumentError(MCFlukeError, ValueError):
    pass


class DeviceIOError(MCFlukeError, OSError):
    pass


class DeviceTimeoutError(MCFlukeError, TimeoutError):
    pass


class ProtocolError(MCFlukeError):
    pass


class CommandRejectedError(MCFlukeError):
    pass


class WrongModelError(MCFlukeError):
    pass


class ConfirmationRequiredError(MCFlukeError):
    pass


class UnsupportedOperationError(MCFlukeError):
    pass


class DeviceClosedError(RuntimeError):
    pass


_ERROR_TYPES = {
    Status.INVALID_ARGUMENT: InvalidArgumentError,
    Status.IO_ERROR: DeviceIOError,
    Status.TIMEOUT: DeviceTimeoutError,
    Status.PROTOCOL_ERROR: ProtocolError,
    Status.COMMAND_REJECTED: CommandRejectedError,
    Status.WRONG_MODEL: WrongModelError,
    Status.CONFIRMATION_REQUIRED: ConfirmationRequiredError,
    Status.UNSUPPORTED: UnsupportedOperationError,
}


def _decode(value):
    return value.decode("utf-8", errors="replace") if value else ""


def _check(library, status, error, operation):
    if status != Status.OK:
        message = _decode(error.message) or _decode(library.mcfluke_status_message(status))
        exception = _ERROR_TYPES.get(status, MCFlukeError)
        raise exception(
            message or "Native operation failed",
            status=status,
            os_code=error.os_code,
            operation=operation,
        )


def _integer(value, name, minimum, maximum):
    try:
        if isinstance(value, bool):
            raise TypeError
        result = operator.index(value)
    except TypeError as exc:
        raise InvalidArgumentError(
            f"{name} must be an integer", status=Status.INVALID_ARGUMENT
        ) from exc
    if not minimum <= result <= maximum:
        raise InvalidArgumentError(
            f"{name} must be in {minimum}..{maximum}", status=Status.INVALID_ARGUMENT
        )
    return result


def _text(value, name):
    if not isinstance(value, str) or "\0" in value:
        raise InvalidArgumentError(
            f"{name} must be a string without NUL characters", status=Status.INVALID_ARGUMENT
        )
    return value.encode("utf-8")


def _confirmation(value):
    if not isinstance(value, bool):
        raise InvalidArgumentError(
            "confirmation must be an explicit bool", status=Status.INVALID_ARGUMENT
        )
    return int(value)


def _identity(result):
    return Identity(
        _decode(result.model), _decode(result.firmware), _decode(result.serial_number)
    )


def _reading(result):
    try:
        state = ReadingState(result.state)
    except ValueError as exc:
        raise ProtocolError(
            f"Native library returned unknown reading state {result.state}",
            status=Status.PROTOCOL_ERROR,
        ) from exc
    return Reading(
        result.value if state == ReadingState.VALUE else None,
        state,
        _decode(result.display),
        _decode(result.unit),
    )


def _release(library, handle, lock):
    with lock:
        library.mcfluke_close(handle)


class MCFluke:
    """A verified FLUKE 187 connection; every device call is serialized."""

    def __init__(self, port="COM3", timeout=1500, *, library_path=None):
        port_bytes = _text(port, "port")
        timeout = _integer(timeout, "timeout", 50, 60000)
        self._lock = threading.RLock()
        self._library = _native.load(library_path)
        self._handle = _native.Handle()
        error = _native.Error()
        status = self._library.mcfluke_open(
            port_bytes, timeout, C.byref(self._handle), C.byref(error)
        )
        if status != Status.OK:
            if self._handle:
                self._library.mcfluke_close(self._handle)
            _check(self._library, status, error, "open")
        if not self._handle:
            raise ProtocolError(
                "Native open succeeded without a device", status=Status.PROTOCOL_ERROR
            )
        self._finalizer = weakref.finalize(
            self, _release, self._library, self._handle, self._lock
        )

    @property
    def closed(self):
        with self._lock:
            return not self._finalizer.alive

    def _ensure_open(self):
        if not self._finalizer.alive:
            raise DeviceClosedError("The MCFluke device is closed.")

    def _call(self, name, *arguments):
        with self._lock:
            self._ensure_open()
            error = _native.Error()
            function = getattr(self._library, f"mcfluke_{name}")
            status = function(self._handle, *arguments, C.byref(error))
            _check(self._library, status, error, name)

    def get_identity(self):
        result = _native.Identity()
        self._call("get_identity", C.byref(result))
        return _identity(result)

    def read_display(self):
        result = _native.Reading()
        self._call("read_display", C.byref(result))
        return _reading(result)

    def read_clock_counter(self):
        """Return the raw unsigned 32-bit decisecond counter, not an epoch."""
        result = C.c_uint32()
        self._call("get_clock", C.byref(result))
        return result.value

    def set_clock(self, time_of_day_ticks):
        """Set integer ticks since midnight; return the exact written raw counter."""
        ticks = _integer(time_of_day_ticks, "time_of_day_ticks", 0, CLOCK_TICKS_PER_DAY - 1)
        result = C.c_uint32()
        self._call("set_clock", ticks, C.byref(result))
        return result.value

    def get_timeout(self):
        result = C.c_uint32()
        self._call("get_timeout", C.byref(result))
        return result.value

    def set_timeout(self, timeout):
        self._call("set_timeout", _integer(timeout, "timeout", 50, 60000))

    def press_button(self, button, *, confirm_calibration=False):
        self._call(
            "press_button",
            _integer(button, "button", -(2**31), 2**31 - 1),
            _confirmation(confirm_calibration),
        )

    def default_setup(self):
        self._call("default_setup")

    def reset_instrument(self, *, confirmation=False):
        self._call("reset_instrument", _confirmation(confirmation))

    def close(self):
        """Close once, waiting for any concurrent native call to finish."""
        with self._lock:
            self._finalizer()

    def __enter__(self):
        with self._lock:
            self._ensure_open()
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        self.close()
        return False


def parse_identity(response, *, library_path=None):
    """Parse an unframed ID payload using the same native parser as the device."""
    data = _text(response, "response")
    library = _native.load(library_path)
    result = _native.Identity()
    error = _native.Error()
    status = library.mcfluke_parse_identity(data, C.byref(result), C.byref(error))
    _check(library, status, error, "parse_identity")
    return _identity(result)


def parse_display(response, *, library_path=None):
    """Parse a full QM line; nonnumeric readings have value=None, never zero."""
    data = _text(response, "response")
    library = _native.load(library_path)
    result = _native.Reading()
    error = _native.Error()
    status = library.mcfluke_parse_display(data, C.byref(result), C.byref(error))
    _check(library, status, error, "parse_display")
    return _reading(result)


def get_version(*, library_path=None):
    library = _native.load(library_path)
    return Version(_decode(library.mcfluke_version()), library.mcfluke_abi_version())


def status_message(status, *, library_path=None):
    status = _integer(status, "status", -(2**31), 2**31 - 1)
    return _decode(_native.load(library_path).mcfluke_status_message(status))


__all__ = [
    "MCFluke", "Identity", "Reading", "Version", "Button", "Status", "ReadingState",
    "CLOCK_TICKS_PER_SECOND", "CLOCK_TICKS_PER_DAY",
    "parse_identity", "parse_display", "get_version", "status_message",
    "DllNotFoundError", "NativeABIError", "MCFlukeError", "InvalidArgumentError",
    "DeviceIOError", "DeviceTimeoutError", "ProtocolError", "CommandRejectedError",
    "WrongModelError", "ConfirmationRequiredError", "UnsupportedOperationError",
    "DeviceClosedError",
]
