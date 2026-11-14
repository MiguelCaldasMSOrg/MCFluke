"""Run already-built offline checks; also serves the Visual Studio checks project."""

import argparse
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def run(command, *, cwd=ROOT, env=None):
    print("\n> " + subprocess.list2cmdline([str(part) for part in command]), flush=True)
    subprocess.run([str(part) for part in command], cwd=cwd, env=env, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--configuration", choices=["Debug", "Release"],
                        default=os.environ.get("MCFLUKE_CONFIGURATION", "Debug"))
    parser.add_argument("--native-dir", type=Path,
                        default=os.environ.get("MCFLUKE_NATIVE_DIR"))
    args = parser.parse_args()
    native = args.native_dir or (
        ROOT / "out" / "vs" / "bin" / "x64" / args.configuration if os.name == "nt"
        else ROOT / "build" / "bin"
    )
    native = native.resolve()
    suffix = ".exe" if os.name == "nt" else ""
    library = native / ("mcfluke.dll" if os.name == "nt"
                        else "libmcfluke.dylib" if sys.platform == "darwin" else "libmcfluke.so")
    managed = ROOT / "csharp" / "MCFluke.Exerciser" / "bin" / args.configuration / "net10.0" / "MCFluke.Exerciser.dll"
    required = [library, managed, *(native / (name + suffix) for name in
                                   ("mcfluke-tests", "mcfluke-example", "mcfluke-host"))]
    missing = [str(path) for path in required if not path.is_file()]
    if missing:
        raise RuntimeError("Build the solution before running offline checks. Missing:\n" + "\n".join(missing))
    env = os.environ.copy()
    env["MCFLUKE_LIBRARY"] = str(library)
    env["MCFLUKE_HOST"] = str(native / ("mcfluke-host" + suffix))
    env["MCFLUKE_NATIVE_DIR"] = str(native)
    env["PYTHONPATH"] = str(ROOT / "python" / "src") + (
        os.pathsep + env["PYTHONPATH"] if env.get("PYTHONPATH") else ""
    )
    run([native / ("mcfluke-tests" + suffix)], env=env)
    run([native / ("mcfluke-example" + suffix), "--port", "sim://187", "--count", "2", "--interval", "0.01"], env=env)
    run([sys.executable, ROOT / "tests" / "test_bridge.py", env["MCFLUKE_HOST"]], env=env)
    run([sys.executable, ROOT / "tests" / "test_serial.py", library], env=env)
    run([sys.executable, "-m", "unittest", "discover", "-s", ROOT / "python" / "tests", "-v"], env=env)
    run(["node", "--test"], cwd=ROOT / "javascript", env=env)
    run(["dotnet", managed, "--self-test"], env=env)
    run([sys.executable, ROOT / "tests" / "verify_examples.py"], env=env)


if __name__ == "__main__":
    try:
        main()
    except (subprocess.CalledProcessError, FileNotFoundError, RuntimeError) as error:
        print(f"Offline checks failed: {error}", file=sys.stderr)
        sys.exit(1)
