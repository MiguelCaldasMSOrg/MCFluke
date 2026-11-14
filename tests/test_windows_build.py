import importlib.util
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


class WindowsBuildTests(unittest.TestCase):
    def test_native_sources_have_no_portability_implementations(self):
        forbidden = (
            r"_POSIX_C_SOURCE|_GNU_SOURCE|__linux__|__APPLE__|__attribute__",
            r"<(?:fcntl|poll|termios|unistd|xlocale)\.h>",
            r"\b(?:clock_gettime|nanosleep|newlocale|freelocale|poll|ssize_t|locale_t|strtod_l)\b",
            r"#\s*(?:if|elif)\s+defined\(_WIN32\)",
        )
        for directory in ("src", "include", "examples", "tests"):
            for source in (ROOT / directory).rglob("*"):
                if source.suffix not in (".c", ".h"):
                    continue
                with self.subTest(source=str(source.relative_to(ROOT))):
                    text = source.read_text(encoding="utf-8")
                    for pattern in forbidden:
                        self.assertNotRegex(text, pattern)
        internal = (ROOT / "src" / "internal.h").read_text(encoding="utf-8")
        self.assertIn("HANDLE handle;", internal)
        self.assertNotIn("int fd;", internal)

    def test_managed_projects_have_one_windows_target_and_mandatory_native_build(self):
        for relative in (
            r"csharp\MCFluke\MCFluke.csproj",
            r"csharp\MCFluke.Exerciser\MCFluke.Exerciser.csproj",
        ):
            with self.subTest(project=relative):
                project = ET.parse(ROOT / relative).getroot()
                self.assertEqual(project.findtext("PropertyGroup/TargetFramework"), "net10.0-windows")
                self.assertIsNone(project.find("PropertyGroup/TargetFrameworks"))
        project = ET.parse(ROOT / "csharp" / "MCFluke" / "MCFluke.csproj").getroot()
        groups = [group for group in project.findall("ItemGroup")
                  if any(reference.get("Include", "").endswith("MCFluke.Native.vcxproj")
                         for reference in group.findall("ProjectReference"))]
        self.assertEqual(len(groups), 1)
        self.assertIsNone(groups[0].get("Condition"))
        for reference in groups[0].findall("ProjectReference"):
            self.assertIsNone(reference.get("Condition"))
        paths = (ROOT / "eng" / "MCFluke.Paths.props").read_text(encoding="utf-8")
        self.assertNotIn("MCFlukeUseVisualStudioNative", paths)
        self.assertNotIn("MSBuildRuntimeType", paths)
        namespace = {"ms": "http://schemas.microsoft.com/developer/msbuild/2003"}
        properties = ET.fromstring(paths)
        guard = properties.find("ms:Target[@Name='MCFlukeRequireWindows']", namespace)
        self.assertIsNotNone(guard)
        self.assertEqual(guard.get("BeforeTargets"), "PrepareForBuild")
        self.assertEqual(
            guard.find("ms:Error", namespace).get("Condition"), "'$(OS)' != 'Windows_NT'"
        )

    def test_non_windows_commands_fail_before_writing_files_or_starting_processes(self):
        cases = (
            (r"tools\build.py", ["--skip-tests"]),
            (r"tools\run_checks.py", ["--native-dir", str(ROOT / "out" / "vs")]),
            (r"tools\verify_vs_build.py", ["--msbuild", "unused.exe"]),
            (r"tests\verify_examples.py", []),
        )
        for relative, arguments in cases:
            with self.subTest(command=relative):
                source = ROOT / relative
                spec = importlib.util.spec_from_file_location(source.stem, source)
                module = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(module)
                with (
                    patch.object(module, "os", SimpleNamespace(name="posix", environ={})),
                    patch.object(sys, "argv", [str(source), *arguments]),
                    patch.object(Path, "mkdir") as mkdir,
                    patch("tempfile.mkdtemp") as temporary,
                    patch("subprocess.run") as run,
                    patch("subprocess.check_output") as output,
                ):
                    with self.assertRaisesRegex(RuntimeError, "Windows"):
                        module.main()
                    output.assert_not_called()
                    run.assert_not_called()
                    temporary.assert_not_called()
                    mkdir.assert_not_called()


if __name__ == "__main__":
    unittest.main()
