import json
from pathlib import Path
import subprocess
import sys
import unittest

import mcfluke as m


class NativeSimulatorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            cls.version = m.get_version()
        except m.DllNotFoundError as exc:
            raise RuntimeError(
                "Native offline-test prerequisite missing: build the MCFluke solution in "
                "Visual Studio 2026 (out\\vs\\bin\\x64\\Debug or Release), or set "
                "MCFLUKE_LIBRARY to its absolute library path. "
                "These tests use only sim://187, never COM3."
            ) from exc

    def test_initial_identity_reading_and_version(self):
        self.assertTrue(self.version.version)
        self.assertEqual(self.version.abi, 1)
        with m.MCFluke("sim://187") as meter:
            self.assertEqual(meter.get_identity(), m.Identity("FLUKE 187", "SIM-1.0", "SIMULATED"))
            reading = meter.read_display()
            self.assertEqual(reading.display, "+1.234 V DC")
            self.assertEqual(reading.unit, "V DC")
            self.assertEqual(reading.state, m.ReadingState.VALUE)
            self.assertAlmostEqual(reading.value, 1.234)
            self.assertEqual(meter.get_timeout(), 1500)
            meter.set_timeout(50)
            self.assertEqual(meter.get_timeout(), 50)
            meter.set_timeout(60000)
            self.assertEqual(meter.get_timeout(), 60000)
        self.assertTrue(m.status_message(m.Status.TIMEOUT))

    def test_native_parsers_preserve_displayed_units_and_explicit_states(self):
        identity = m.parse_identity("FLUKE 187,SIM-1.0,SIMULATED")
        self.assertEqual(identity, m.Identity("FLUKE 187", "SIM-1.0", "SIMULATED"))
        numeric = m.parse_display("QM,-12.5 mV DC")
        self.assertEqual(numeric.display, "-12.5 mV DC")
        self.assertEqual(numeric.unit, "mV DC")
        self.assertEqual(numeric.value, -12.5)
        self.assertEqual(numeric.state, m.ReadingState.VALUE)
        overload = m.parse_display("QM,Out of range")
        self.assertEqual(overload.display, "Out of range")
        self.assertEqual(overload.state, m.ReadingState.OUT_OF_RANGE)
        self.assertIsNone(overload.value)
        text = m.parse_display("QM,LEADS")
        self.assertEqual(text.display, "LEADS")
        self.assertEqual(text.state, m.ReadingState.TEXT)
        self.assertIsNone(text.value)
        zero = m.parse_display("QM,0 V DC")
        self.assertEqual(zero.state, m.ReadingState.VALUE)
        self.assertEqual(zero.value, 0)
        for parser, malformed in ((m.parse_identity, "not an ID"), (m.parse_display, "not QM")):
            with self.subTest(malformed=malformed):
                with self.assertRaises(m.MCFlukeError):
                    parser(malformed)

    def test_simulator_control_guards_and_operations_only(self):
        with m.MCFluke("sim://187") as meter:
            with self.assertRaises(m.ConfirmationRequiredError):
                meter.reset_instrument()
            with self.assertRaises(m.ConfirmationRequiredError):
                meter.press_button(m.Button.CALIBRATION)
            for button in (m.Button.LOGGING, m.Button.SAVE):
                with self.subTest(button=button):
                    with self.assertRaises(m.UnsupportedOperationError):
                        meter.press_button(button)
            with self.assertRaises(m.InvalidArgumentError):
                meter.press_button(999)
            meter.press_button(m.Button.RANGE)
            meter.default_setup()
            meter.press_button(m.Button.CALIBRATION, confirm_calibration=True)
            meter.reset_instrument(confirmation=True)
            self.assertEqual(meter.read_display().state, m.ReadingState.VALUE)

    def test_connections_independent_and_disposal(self):
        first = m.MCFluke("sim://187")
        with m.MCFluke("sim://187") as second:
            first.close()
            first.close()
            with self.assertRaises(m.DeviceClosedError):
                first.read_display()
            self.assertEqual(second.get_identity().model, "FLUKE 187")

    def test_clock_set_and_running_unsigned_readback(self):
        with m.MCFluke("sim://187") as meter:
            initial = meter.read_clock_counter()
            self.assertIs(type(initial), int)
            self.assertGreaterEqual(initial, 0)
            self.assertLessEqual(initial, 4294967295)
            for ticks in (123456, 863999, 0):
                with self.subTest(ticks=ticks):
                    written = meter.set_clock(ticks)
                    self.assertIs(type(written), int)
                    self.assertGreaterEqual(written, 0)
                    self.assertLessEqual(written, 4294967295)
                    self.assertEqual(written % m.CLOCK_TICKS_PER_DAY, ticks)
                    readback = meter.read_clock_counter()
                    self.assertGreaterEqual(readback, 0)
                    self.assertLessEqual(readback, 4294967295)
                    self.assertLess((readback - written) & 0xFFFFFFFF, 10)
            self.assertEqual(meter.get_identity().model, "FLUKE 187")
            self.assertEqual(meter.read_display().display, "+1.234 V DC")
        with self.assertRaises(m.DeviceClosedError):
            meter.read_clock_counter()
        with self.assertRaises(m.DeviceClosedError):
            meter.set_clock(0)

    def test_simulator_reset_restarts_running_clock(self):
        with m.MCFluke("sim://187") as meter:
            meter.set_clock(432000)
            meter.reset_instrument(confirmation=True)
            self.assertLess(meter.read_clock_counter(), 10)

    def test_bounded_read_only_example(self):
        example = Path(__file__).resolve().parents[1] / "examples" / "read_display.py"
        for entrypoint in (["-m", "mcfluke"], [str(example)]):
            with self.subTest(entrypoint=entrypoint):
                result = subprocess.run(
                    [sys.executable, *entrypoint, "--port", "sim://187", "--count", "2", "--interval", "0"],
                    capture_output=True, text=True, timeout=15,
                )
                self.assertEqual(result.returncode, 0, result.stderr)
                lines = [json.loads(line) for line in result.stdout.splitlines()]
                self.assertEqual(len(lines), 3)
                self.assertEqual(lines[0]["identity"]["model"], "FLUKE 187")
                self.assertEqual(lines[1]["display"], "+1.234 V DC")
                self.assertEqual(lines[2]["value"], 1.234)


if __name__ == "__main__":
    unittest.main()
