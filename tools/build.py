"""CI helper: Visual Studio solution on Windows, system C compiler on POSIX."""

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
    parser.add_argument("--skip-tests", action="store_true", help="Build only; never opens a hardware port")
    args = parser.parse_args()
    project = ROOT / "csharp" / "MCFluke.Exerciser" / "MCFluke.Exerciser.csproj"
    env = os.environ.copy()
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    if os.name == "nt":
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
    else:
        native = ROOT / "build" / "bin"
        native.mkdir(parents=True, exist_ok=True)
        compiler = os.environ.get("CC") or shutil.which("cc")
        if not compiler:
            raise RuntimeError("A system C11 compiler is required.")
        common = [compiler, "-std=c11", "-Wall", "-Wextra", "-Wpedantic", "-Werror",
                  "-I", ROOT / "include", "-I", ROOT / "src", "-I", ROOT / "tests"]
        common += ["-g", "-O0"] if args.configuration == "Debug" else ["-O2", "-DNDEBUG"]
        library = native / ("libmcfluke.dylib" if sys.platform == "darwin" else "libmcfluke.so")
        shared = ["-dynamiclib", "-Wl,-install_name,@rpath/libmcfluke.dylib"] if sys.platform == "darwin" else ["-shared"]
        run([*common, "-fPIC", "-fvisibility=hidden", "-DMCFLUKE_BUILD", *shared,
             ROOT / "src" / "mcfluke.c", ROOT / "src" / "transport.c", "-o", library])
        rpath = "@loader_path" if sys.platform == "darwin" else "$ORIGIN"
        for name, source in [("mcfluke-example", ROOT / "examples" / "c" / "main.c"),
                             ("mcfluke-host", ROOT / "src" / "host.c")]:
            run([*common, source, "-L", native, "-lmcfluke", f"-Wl,-rpath,{rpath}", "-o", native / name])
        run([*common, "-DMCFLUKE_BUILD", ROOT / "tests" / "test_mcfluke.c",
             ROOT / "tests" / "fake_transport.c", ROOT / "src" / "mcfluke.c",
             "-o", native / "mcfluke-tests"])
        run(["dotnet", "build", project, "--configuration", args.configuration, "--nologo"], env=env)
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
