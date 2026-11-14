"""Exercise the actual native bridge without opening a hardware port."""

import json
from pathlib import Path
import subprocess
import sys
import unittest

HOST = Path(sys.argv.pop(1)).resolve()


def exchange(requests):
    completed = subprocess.run(
        [str(HOST), "--port", "sim://187"],
        input="".join(requests),
        capture_output=True,
        text=True,
        timeout=10,
        check=True,
    )
    return [json.loads(line) for line in completed.stdout.splitlines()]


class BridgeTests(unittest.TestCase):
    def test_clock_operations_preserve_measurement_mode(self):
        messages = exchange([
            "1\tbutton\t10 0\n", "2\tbutton\t17 0\n",
            "3\tset-clock\t432000\n", "4\tclock\t\n", "5\tdisplay\t\n",
            "6\tset-clock\t864000\n", "7\tclose\t\n",
        ])[1:]
        written = messages[2]["result"]
        self.assertEqual(written % 864000, 432000)
        self.assertLess((messages[3]["result"] - written) & 0xFFFFFFFF, 100)
        self.assertEqual(messages[4]["result"]["display"], "+1234.0 mV AC")
        self.assertFalse(messages[5]["ok"])
        self.assertEqual(messages[5]["error"]["status"], 1)

    def test_round_trip(self):
        messages = exchange([
            "1\tid\t\n", "2\tdisplay\t\n", "3\tversion\t\n",
            "4\tget-timeout\t\n", "5\tset-timeout\t250\n",
            "6\tget-timeout\t\n", "7\tclose\t\n",
        ])
        self.assertEqual(messages[0], {"type": "ready", "ok": True})
        self.assertEqual(messages[1]["result"]["model"], "FLUKE 187")
        self.assertEqual(messages[2]["result"]["value"], 1.234)
        self.assertEqual(messages[2]["result"]["state"], 0)
        self.assertEqual(messages[3]["result"]["abi"], 1)
        self.assertEqual(messages[4]["result"], 1500)
        self.assertEqual(messages[6]["result"], 250)
        self.assertIsNone(messages[7]["result"])

    def test_guarded_and_unsupported_commands(self):
        messages = exchange([
            "1\treset\t0\n", "2\tbutton\t20 0\n", "3\tbutton\t23 0\n",
            "4\tbutton\t30 0\n", "5\treset\t1\n", "6\tbutton\t20 1\n",
            "7\tdefaults\t\n", "8\tclose\t\n",
        ])[1:]
        self.assertEqual([m["error"]["status"] for m in messages[:4]], [8, 8, 9, 9])
        self.assertTrue(all(m["ok"] for m in messages[4:]))

    def test_text_range_and_json_escaping(self):
        messages = exchange([
            '1\tparse-display\tQM,Unknown "display" \\\n',
            "2\tparse-display\tQM,Out of Range mVDC\n",
            "3\tparse-display\tQM,-121.43 VDC\n",
            "4\tparse-id\tFLUKE 187, V2.02, 1234\n",
            "5\tstatus-message\t8\n", "6\tclose\t\n",
        ])[1:]
        self.assertEqual(messages[0]["result"]["display"], 'Unknown "display" \\')
        self.assertIsNone(messages[0]["result"]["value"])
        self.assertEqual(messages[0]["result"]["state"], 2)
        self.assertIsNone(messages[1]["result"]["value"])
        self.assertEqual(messages[1]["result"]["state"], 1)
        self.assertEqual(messages[2]["result"]["value"], -121.43)
        self.assertEqual(messages[3]["result"]["serialNumber"], "1234")
        self.assertIn("confirmation", messages[4]["result"])

    def test_malformed_requests_recover(self):
        messages = exchange([
            "not a request\n", "0\tid\t\n", "1\tno-such-command\t\n",
            "2\tid\tunexpected\n", "3\tbutton\t17 0 extra\n",
            "4\tparse-display\tbad\n", "5\tset-timeout\t-1\n",
            "6\tset-timeout\t60001\n", "7\tid\t\n", "8\tclose\t\n",
        ])[1:]
        self.assertTrue(all(not m["ok"] for m in messages[:8]))
        self.assertTrue(all(m["ok"] for m in messages[8:]))

    def test_oversized_request_recovers(self):
        messages = exchange(["1\tparse-display\t" + "x" * 2048 + "\n", "2\tclose\t\n"])
        self.assertFalse(messages[1]["ok"])
        self.assertIn("1023", messages[1]["error"]["message"])
        self.assertTrue(messages[2]["ok"])

    def test_binary_and_cr_input_do_not_consume_next_request(self):
        messages = exchange(["1\tid\t\x00\n", "2\tid\t\r\n", "3\tid\t\n", "4\tclose\t\n"])
        self.assertFalse(messages[1]["ok"])
        self.assertFalse(messages[2]["ok"])
        self.assertEqual(messages[3]["result"]["model"], "FLUKE 187")
        self.assertTrue(messages[4]["ok"])


if __name__ == "__main__":
    unittest.main()
