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

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("MCFluke requires Windows.");
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

        const string fileName = "mcfluke.dll";
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, fileName) };
        string? configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        for (DirectoryInfo? ancestor = new(AppContext.BaseDirectory); ancestor is not null; ancestor = ancestor.Parent)
        {
            foreach (string build in new[] { configuration, "Debug", "Release" }.OfType<string>().Distinct())
            {
                candidates.Add(Path.Combine(ancestor.FullName, "out", "vs", "bin", "x64", build, fileName));
            }
        }

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
            {
                return handle;
            }
        }

        throw new DllNotFoundException(
            $"Cannot load {fileName}. Build MCFluke.sln in Visual Studio on Windows or set MCFLUKE_LIBRARY " +
            $"to its absolute path. Searched:{Environment.NewLine}{string.Join(Environment.NewLine, candidates)}");
    }
}
