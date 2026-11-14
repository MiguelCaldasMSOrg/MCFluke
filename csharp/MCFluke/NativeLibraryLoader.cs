using System.Reflection;
using System.Runtime.InteropServices;

namespace MCFluke;

internal static class NativeLibraryLoader
{
    internal static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != NativeMethods.LibraryName)
        {
            return IntPtr.Zero;
        }

        string? configuredPath = Environment.GetEnvironmentVariable("MCFLUKE_LIBRARY");
        if (!string.IsNullOrEmpty(configuredPath))
        {
            if (!Path.IsPathFullyQualified(configuredPath))
            {
                throw new DllNotFoundException(
                    "MCFLUKE_LIBRARY must be an absolute path to the native MCFluke library.");
            }

            // An explicit override is authoritative; never silently load another build.
            return NativeLibrary.Load(configuredPath);
        }

        string fileName = OperatingSystem.IsWindows() ? "mcfluke.dll"
            : OperatingSystem.IsMacOS() ? "libmcfluke.dylib"
            : "libmcfluke.so";
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, fileName) };
        for (DirectoryInfo? ancestor = new(AppContext.BaseDirectory); ancestor is not null; ancestor = ancestor.Parent)
        {
            candidates.Add(Path.Combine(ancestor.FullName, "build", "bin", fileName));
        }

        foreach (string candidate in candidates.Distinct(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
            {
                return handle;
            }
        }

        throw new DllNotFoundException(
            $"Cannot load {fileName}. Build the native CMake target or set MCFLUKE_LIBRARY " +
            $"to its absolute path. Searched:{Environment.NewLine}{string.Join(Environment.NewLine, candidates)}");
    }
}
