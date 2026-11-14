"""Private, lazy cdecl declarations matching include/mcfluke.h."""

import ctypes as C
import os
from pathlib import Path
import sys
import threading


ABI_VERSION = 1


class DllNotFoundError(OSError):
    """The native library is missing or cannot be loaded with its dependencies."""


class NativeABIError(OSError):
    """The native library does not implement the expected ABI."""


class DeviceHandle(C.Structure):
    pass


Handle = C.POINTER(DeviceHandle)


class Error(C.Structure):
    _fields_ = [
        ("status", C.c_int32),
        ("os_code", C.c_int32),
        ("message", C.c_char * 256),
    ]


class Identity(C.Structure):
    _fields_ = [
        ("model", C.c_char * 32),
        ("firmware", C.c_char * 32),
        ("serial_number", C.c_char * 64),
    ]


class Reading(C.Structure):
    _fields_ = [
        ("value", C.c_double),
        ("state", C.c_int32),
        ("display", C.c_char * 128),
        ("unit", C.c_char * 32),
    ]


def _names():
    if sys.platform == "win32":
        return ("mcfluke.dll",)
    if sys.platform == "darwin":
        return ("libmcfluke.dylib",)
    return ("libmcfluke.so", "libmcfluke.so.1")


def _candidates():
    seen = set()
    package = Path(__file__).resolve().parent
    ancestors = (package, *package.parents)
    for ancestor in ancestors:
        for configuration in ("Debug", "Release"):
            directory = ancestor / "out" / "vs" / "bin" / "x64" / configuration
            for name in _names():
                candidate = directory / name
                if candidate not in seen and candidate.is_file():
                    seen.add(candidate)
                    yield str(candidate)
    for ancestor in ancestors:
        for directory in (ancestor, ancestor / "build" / "bin"):
            for name in _names():
                candidate = directory / name
                if candidate not in seen and candidate.is_file():
                    seen.add(candidate)
                    yield str(candidate)
    for directory in (Path(sys.prefix) / "lib", Path(sys.prefix) / "bin"):
        for name in _names():
            candidate = directory / name
            if candidate not in seen and candidate.is_file():
                seen.add(candidate)
                yield str(candidate)
    # On macOS, importing ctypes.util itself loads libc.
    from ctypes.util import find_library

    installed = find_library("mcfluke")
    if installed:
        yield installed
    yield from _names()


class Library:
    def __init__(self, dll):
        self.dll = dll
        error = C.POINTER(Error)
        handle = Handle
        signatures = {
            "mcfluke_version": (C.c_char_p, []),
            "mcfluke_abi_version": (C.c_uint32, []),
            "mcfluke_status_message": (C.c_char_p, [C.c_int32]),
            "mcfluke_open": (
                C.c_int32, [C.c_char_p, C.c_uint32, C.POINTER(handle), error]
            ),
            "mcfluke_close": (None, [handle]),
            "mcfluke_get_identity": (
                C.c_int32, [handle, C.POINTER(Identity), error]
            ),
            "mcfluke_read_display": (
                C.c_int32, [handle, C.POINTER(Reading), error]
            ),
            "mcfluke_get_clock": (
                C.c_int32, [handle, C.POINTER(C.c_uint32), error]
            ),
            "mcfluke_set_clock": (
                C.c_int32, [handle, C.c_uint32, C.POINTER(C.c_uint32), error]
            ),
            "mcfluke_get_timeout": (
                C.c_int32, [handle, C.POINTER(C.c_uint32), error]
            ),
            "mcfluke_set_timeout": (C.c_int32, [handle, C.c_uint32, error]),
            "mcfluke_press_button": (
                C.c_int32, [handle, C.c_int32, C.c_int32, error]
            ),
            "mcfluke_default_setup": (C.c_int32, [handle, error]),
            "mcfluke_reset_instrument": (C.c_int32, [handle, C.c_int32, error]),
            "mcfluke_parse_identity": (
                C.c_int32, [C.c_char_p, C.POINTER(Identity), error]
            ),
            "mcfluke_parse_display": (
                C.c_int32, [C.c_char_p, C.POINTER(Reading), error]
            ),
        }
        try:
            for name, (result, arguments) in signatures.items():
                function = getattr(dll, name)
                function.restype = result
                function.argtypes = arguments
                setattr(self, name, function)
        except AttributeError as exc:
            raise NativeABIError(f"MCFluke library is missing an ABI symbol: {exc}") from exc
        abi = self.mcfluke_abi_version()
        if abi != ABI_VERSION:
            raise NativeABIError(
                f"MCFluke ABI {abi} is incompatible; expected ABI {ABI_VERSION}."
            )


_libraries = {}
_libraries_lock = threading.Lock()


def load(library_path=None):
    override = library_path if library_path is not None else os.environ.get("MCFLUKE_LIBRARY")
    if override is not None:
        override = os.fspath(override)
        if not override or not Path(override).is_absolute():
            raise DllNotFoundError("MCFLUKE_LIBRARY/library_path must be an absolute file path.")
        if not Path(override).is_file():
            raise DllNotFoundError(f"MCFluke native library does not exist: {override}")
    key = override or "<installed-or-repository>"
    with _libraries_lock:
        if key in _libraries:
            return _libraries[key]
        failures = []
        for candidate in (override,) if override else _candidates():
            try:
                # The public API is cdecl, including on Windows.
                dll = C.CDLL(candidate)
            except OSError as exc:
                failures.append(f"{candidate}: {exc}")
                continue
            library = Library(dll)
            _libraries[key] = library
            return library
        detail = "\n".join(failures)
        raise DllNotFoundError(
            "Cannot load the MCFluke native library. Build the MCFluke solution in "
            "Visual Studio 2026 (out/vs/bin/x64/Debug or Release), install the library "
            "on the OS library search path, or set "
            "MCFLUKE_LIBRARY to its absolute path. Check architecture and dependent "
            "libraries as well; Windows requires 64-bit Python. Legacy build/bin "
            f"remains a fallback.\n{detail}"
        )
