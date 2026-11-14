"""Build every binding and run the offline suite without installing dependencies."""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def run(command, *, cwd=ROOT, env=None):
    print("\n> " + subprocess.list2cmdline([str(part) for part in command]), flush=True)
    subprocess.run([str(part) for part in command], cwd=cwd, env=env, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--configuration", choices=["Debug", "Release"], default="Release")
    parser.add_argument("--generator", "-G", help="CMake generator (reuses an existing build cache by default)")
    parser.add_argument("--skip-tests", action="store_true", help="Build only; never opens a hardware port")
    args = parser.parse_args()
    build = ROOT / "build"
    generator = args.generator
    if (
        not generator and os.name == "nt" and not (build / "CMakeCache.txt").exists()
        and shutil.which("gcc") and shutil.which("mingw32-make")
    ):
        generator = "MinGW Makefiles"
    configure = ["cmake", "-S", ROOT, "-B", build, f"-DCMAKE_BUILD_TYPE={args.configuration}"]
    if generator:
        configure += ["-G", generator]
    run(configure)
    run(["cmake", "--build", build, "--config", args.configuration, "--parallel"])
    project = ROOT / "csharp" / "MCFluke.Exerciser" / "MCFluke.Exerciser.csproj"
    env = os.environ.copy()
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    run(["dotnet", "build", project, "--configuration", args.configuration, "--nologo"], env=env)
    if args.skip_tests:
        return
    name = "mcfluke.dll" if os.name == "nt" else "libmcfluke.dylib" if sys.platform == "darwin" else "libmcfluke.so"
    env["MCFLUKE_LIBRARY"] = str(build / "bin" / name)
    env["MCFLUKE_HOST"] = str(build / "bin" / ("mcfluke-host.exe" if os.name == "nt" else "mcfluke-host"))
    env["PYTHONPATH"] = str(ROOT / "python" / "src") + (
        os.pathsep + env["PYTHONPATH"] if env.get("PYTHONPATH") else ""
    )
    run(["ctest", "--test-dir", build, "-C", args.configuration, "--output-on-failure"], env=env)
    run([sys.executable, "-m", "unittest", "discover", "-s", ROOT / "python" / "tests", "-v"], env=env)
    run(["node", "--test"], cwd=ROOT / "javascript", env=env)
    run(["dotnet", "run", "--project", project, "--configuration", args.configuration,
         "--no-build", "--", "--self-test"], env=env)
    run([sys.executable, ROOT / "tests" / "verify_examples.py"], env=env)


if __name__ == "__main__":
    try:
        main()
    except (subprocess.CalledProcessError, FileNotFoundError) as error:
        print(f"Build/check failed: {error}", file=sys.stderr)
        sys.exit(1)
