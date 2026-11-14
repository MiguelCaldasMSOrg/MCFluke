"""Read-only example: identify once, then print the primary display."""

import argparse
from dataclasses import asdict
import json
import math
import sys
import time

from . import DllNotFoundError, MCFluke, MCFlukeError, NativeABIError


def _interval(value):
    number = float(value)
    if not math.isfinite(number) or number < 0:
        raise argparse.ArgumentTypeError("interval must be a finite nonnegative number")
    return number


def _count(value):
    number = int(value)
    if number < 0:
        raise argparse.ArgumentTypeError("count must be nonnegative")
    return number


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", default="COM3", help="serial port or explicit sim://187")
    parser.add_argument("--count", type=_count, help="stop after N readings (default: forever)")
    parser.add_argument("--interval", type=_interval, default=2.0, help="seconds between reads")
    parser.add_argument("--timeout", type=int, default=1500, help="native timeout, 50..60000 ms")
    arguments = parser.parse_args(argv)
    try:
        with MCFluke(arguments.port, arguments.timeout) as meter:
            print(json.dumps({"identity": asdict(meter.get_identity())}), flush=True)
            number = 0
            while arguments.count is None or number < arguments.count:
                if number:
                    time.sleep(arguments.interval)
                print(json.dumps(asdict(meter.read_display())), flush=True)
                number += 1
        return 0
    except KeyboardInterrupt:
        print("Interrupted; the MCFluke connection has been closed.", file=sys.stderr)
        return 130
    except (MCFlukeError, DllNotFoundError, NativeABIError) as exc:
        print(f"MCFluke: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
