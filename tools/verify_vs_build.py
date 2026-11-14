"""Verify solution build/copy contracts in an isolated temporary source copy."""

import argparse
import hashlib
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def verify_native_discovery(library, application, environment):
    with tempfile.TemporaryDirectory(prefix="MCFluke-discovery-") as directory:
        root = Path(directory)
        app = root / "app"
        app.mkdir()
        for name in ("MCFluke.Exerciser.dll", "MCFluke.Exerciser.deps.json",
                     "MCFluke.Exerciser.runtimeconfig.json", "MCFluke.Managed.dll"):
            shutil.copy2(application.parent / name, app / name)
        retired = root / "build" / "bin" / library.name
        retired.parent.mkdir(parents=True)
        shutil.copy2(library, retired)

        def check(available, override=None):
            env = environment.copy()
            env.pop("MCFLUKE_LIBRARY", None)
            if override is not None:
                env["MCFLUKE_LIBRARY"] = str(override)
            result = subprocess.run(
                ["dotnet", str(app / application.name), "--self-test"],
                cwd=root, env=env, capture_output=True, text=True, timeout=30,
            )
            if available:
                if result.returncode:
                    raise RuntimeError(f"Native discovery failed:\n{result.stdout}\n{result.stderr}")
            elif (result.returncode != 1
                  or "BLOCKED native simulator tests" not in result.stderr
                  or str(retired).casefold() in result.stderr.casefold()):
                raise RuntimeError(f"Retired output was not rejected:\n{result.stdout}\n{result.stderr}")

        check(False)
        current = root / "out" / "vs" / "bin" / "x64" / "Debug" / library.name
        current.parent.mkdir(parents=True)
        shutil.copy2(library, current)
        check(True)
        current.unlink()
        adjacent = app / library.name
        shutil.copy2(library, adjacent)
        check(True)
        adjacent.unlink()
        check(True, retired)
    print("PASS managed native discovery: current output, adjacent deployment, explicit override; "
          "retired output is not searched.", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--msbuild", required=True, type=Path)
    parser.add_argument("--keep", action="store_true", help="Keep the isolated copy for IDE inspection")
    args = parser.parse_args()
    if os.name != "nt":
        raise RuntimeError("MCFluke build verification requires Windows and Visual Studio 2026.")
    if not (ROOT / "MCFluke.sln").read_bytes().startswith(b"\xef\xbb\xbf\r\nMicrosoft Visual Studio Solution File"):
        raise RuntimeError("Preserve the SDK-style solution BOM, leading line and CRLF encoding.")
    # MSVC deliberately excludes %TEMP% from reliable file tracking.
    copied = Path(tempfile.mkdtemp(prefix="MCFluke-vs-", dir=ROOT.parent))
    print(f"Isolated verification copy: {copied}", flush=True)
    completed = False
    try:
        names = subprocess.check_output(
            ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
            cwd=ROOT,
        ).decode().split("\0")
        inputs = {}
        for name in set(names):
            source = ROOT / name
            if not name or not source.is_file():
                continue
            destination = copied / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, destination)
            inputs[name] = digest(source)
            destination.chmod(stat.S_IREAD)
        logs = copied / "out" / "verification"
        logs.mkdir(parents=True)

        def build(configuration, label, target="Build"):
            command = [
                str(args.msbuild), str(copied / "MCFluke.sln"), "/m", "/nologo",
                "/verbosity:minimal", f"/t:{target}", f"/p:Configuration={configuration}",
                "/p:Platform=x64",
            ]
            if label == "fresh-debug":
                command.append("/restore")
            with (logs / f"{label}.log").open("w", encoding="utf-8") as output:
                result = subprocess.run(command, cwd=copied, stdout=output, stderr=subprocess.STDOUT)
            if result.returncode:
                print((logs / f"{label}.log").read_text(encoding="utf-8"), flush=True)
                raise RuntimeError(f"{label} failed with code {result.returncode}")

        def native(configuration):
            return copied / "out" / "vs" / "bin" / "x64" / configuration / "mcfluke.dll"

        def consumer(configuration):
            return copied / "csharp" / "MCFluke.Exerciser" / "bin" / configuration / "net10.0-windows" / "mcfluke.dll"

        def verify_copy(configuration):
            if digest(native(configuration)) != digest(consumer(configuration)):
                raise RuntimeError(f"{configuration} consumer did not get its matching native DLL")

        build("Debug", "fresh-debug")
        verify_copy("Debug")
        stamp = native("Debug").stat().st_mtime_ns
        copy_stamp = consumer("Debug").stat().st_mtime_ns
        build("Debug", "unchanged-debug")
        if native("Debug").stat().st_mtime_ns != stamp or consumer("Debug").stat().st_mtime_ns != copy_stamp:
            raise RuntimeError(
                f"Unchanged build replaced a DLL: native {stamp}->{native('Debug').stat().st_mtime_ns}; "
                f"consumer {copy_stamp}->{consumer('Debug').stat().st_mtime_ns}"
            )
        consumer("Debug").unlink()
        build("Debug", "missing-consumer")
        verify_copy("Debug")
        consumer("Debug").write_bytes(b"newer incorrect copied DLL")
        future = native("Debug").stat().st_mtime + 3600
        os.utime(consumer("Debug"), (future, future))
        build("Debug", "newer-consumer")
        verify_copy("Debug")
        native("Debug").unlink()
        build("Debug", "missing-native")
        verify_copy("Debug")
        debug_hash = digest(native("Debug"))
        debug_stamp = native("Debug").stat().st_mtime_ns
        build("Release", "release")
        verify_copy("Release")
        if digest(native("Release")) == debug_hash:
            raise RuntimeError("Debug and Release unexpectedly produced an identical native DLL")
        build("Debug", "switch-back-debug")
        verify_copy("Debug")
        if digest(native("Debug")) != debug_hash or native("Debug").stat().st_mtime_ns != debug_stamp:
            raise RuntimeError("Switching back did not preserve the separate Debug output")
        environment = os.environ.copy()
        environment.pop("MCFLUKE_LIBRARY", None)
        subprocess.run(
            ["dotnet", str(consumer("Debug").with_name("MCFluke.Exerciser.dll")), "--self-test"],
            cwd=copied, env=environment, check=True,
        )
        verify_native_discovery(
            native("Debug"), consumer("Debug").with_name("MCFluke.Exerciser.dll"), environment
        )
        build("Debug", "clean-debug", "Clean")
        for name, expected in inputs.items():
            if digest(copied / name) != expected or digest(ROOT / name) != expected:
                raise RuntimeError(f"A build changed a source input: {name}")
        completed = True
        print("PASS fresh/no-change builds, missing/newer copies, missing native output, "
              "Debug/Release switching, copied-DLL runtime use, Clean, and read-only source hashes.", flush=True)
    finally:
        if completed and not args.keep:
            for path in copied.rglob("*"):
                if path.is_file():
                    path.chmod(stat.S_IWRITE | stat.S_IREAD)
            shutil.rmtree(copied)
            print(f"Removed isolated verification copy: {copied}", flush=True)
        else:
            print(f"Retained verification copy and logs: {copied}", flush=True)


if __name__ == "__main__":
    main()
