"""Check the real default two-second output cadence of the C/Python/JS examples."""

import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time

ROOT = Path(__file__).resolve().parents[1]


def verify(name, command):
    process = subprocess.Popen(
        [str(part) for part in command],
        cwd=ROOT,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1,
    )
    lines = queue.Queue()

    def collect():
        for line in process.stdout:
            lines.put((time.monotonic(), line.rstrip()))
        lines.put(None)

    reader = threading.Thread(target=collect, daemon=True)
    reader.start()
    times, output = [], []
    try:
        deadline = time.monotonic() + 15
        while True:
            if time.monotonic() >= deadline:
                raise RuntimeError(f"{name} exceeded its 15-second deadline")
            item = lines.get(timeout=max(0.01, deadline - time.monotonic()))
            if item is None:
                break
            timestamp, line = item
            output.append(line)
            if "1.234 V DC" in line:
                times.append(timestamp)
        process.wait(timeout=2)
        diagnostic = process.stderr.read()
        if process.returncode:
            raise RuntimeError(f"{name} exited {process.returncode}: {diagnostic}\n{output}")
        if not any("FLUKE 187" in line for line in output) or len(times) != 3:
            raise RuntimeError(f"{name} did not print meter ID and exactly three display readings: {output}")
        intervals = [right - left for left, right in zip(times, times[1:])]
        if any(not 1.7 <= interval <= 2.7 for interval in intervals):
            raise RuntimeError(f"{name} default cadence is not two seconds: {intervals}")
        print(f"{name}: ID, three primary displays; intervals {intervals[0]:.3f}s, {intervals[1]:.3f}s")
    except queue.Empty as error:
        raise RuntimeError(f"{name} did not finish its bounded example in 15 seconds") from error
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=2)
        reader.join(timeout=2)
        process.stdout.close()
        process.stderr.close()


def main():
    native = ROOT / "build" / "bin" / ("mcfluke-example.exe" if os.name == "nt" else "mcfluke-example")
    verify("C", [native, "--port", "sim://187", "--count", "3"])
    verify("Python", [sys.executable, "-I", ROOT / "python" / "examples" / "read_display.py",
                      "--port", "sim://187", "--count", "3"])
    verify("JavaScript", ["node", ROOT / "javascript" / "examples" / "read-display.js",
                          "--port", "sim://187", "--count", "3"])


if __name__ == "__main__":
    main()
