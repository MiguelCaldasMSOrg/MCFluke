"""Windows CI helper for the Visual Studio 2026 solution."""

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
    parser.add_argument("--configuration", choices=["Debug", "Release"], default="Release")
    parser.add_argument("--skip-tests", action="store_true", help="Build only; never opens a hardware port")
    args = parser.parse_args()
    if os.name != "nt":
        raise RuntimeError("MCFluke builds require Windows and Visual Studio 2026.")
    env = os.environ.copy()
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    vswhere = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Microsoft Visual Studio" / "Installer" / "vswhere.exe"
    installation = subprocess.check_output(
        [str(vswhere), "-latest", "-version", "[18.0,19.0)", "-products", "*",
         "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
         "-property", "installationPath"], text=True
    ).strip()
    if not installation:
        raise RuntimeError("Visual Studio 2026 with Desktop C++ is required.")
    msbuild = Path(installation) / "MSBuild" / "Current" / "Bin" / "MSBuild.exe"
    run([msbuild, ROOT / "eng" / "MCFluke.Build.slnf", "/restore", "/m", "/nologo", "/verbosity:minimal",
         f"/p:Configuration={args.configuration}", "/p:Platform=x64"], env=env)
    native = ROOT / "out" / "vs" / "bin" / "x64" / args.configuration
    if args.skip_tests:
        return
    run([sys.executable, ROOT / "tools" / "run_checks.py", "--configuration", args.configuration,
         "--native-dir", native], env=env)


if __name__ == "__main__":
    try:
        main()
    except (subprocess.CalledProcessError, FileNotFoundError, RuntimeError) as error:
        print(f"Build/check failed: {error}", file=sys.stderr)
        sys.exit(1)
