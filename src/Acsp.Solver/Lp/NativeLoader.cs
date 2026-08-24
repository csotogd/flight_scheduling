using System.Runtime.InteropServices;

namespace Acsp.Solver.Lp;

/// <summary>
/// Single DllImport resolver for the solver backends (.NET allows only one per assembly).
/// Maps "highs" and "cplex" to locally installed shared libraries; returning IntPtr.Zero
/// falls through to the default OS library search.
/// </summary>
internal static class NativeLoader
{
    public static readonly Lazy<IntPtr> Highs = new(() => Load(HighsCandidates()));
    public static readonly Lazy<IntPtr> Cplex = new(() => Load(CplexCandidates()));

    static NativeLoader()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeLoader).Assembly, (name, _, _) => name switch
        {
            "highs" => Highs.Value,
            "cplex" => Cplex.Value,
            _ => IntPtr.Zero,
        });
    }

    /// <summary>Forces the static constructor so the resolver is registered.</summary>
    public static void EnsureRegistered() { }

    private static IntPtr Load(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
            if (candidate.Length > 0 && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        return IntPtr.Zero;
    }

    private static IEnumerable<string> HighsCandidates() =>
    [
        Environment.GetEnvironmentVariable("ACSP_LIBHIGHS") ?? "",
        "/opt/homebrew/lib/libhighs.dylib",
        "/usr/local/lib/libhighs.dylib",
        "libhighs.so", "libhighs.dylib", "highs.dll",
    ];

    private static IEnumerable<string> CplexCandidates()
    {
        var env = Environment.GetEnvironmentVariable("ACSP_LIBCPLEX");
        if (!string.IsNullOrEmpty(env)) yield return env;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] roots =
        [
            Path.Combine(home, "Applications"), "/Applications", "/opt/ibm/ILOG",
            programFiles.Length > 0 ? Path.Combine(programFiles, "IBM", "ILOG") : "",
        ];
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var studio in Directory.GetDirectories(root, "CPLEX_Studio*").OrderDescending())
            {
                var bin = Path.Combine(studio, "cplex", "bin");
                if (!Directory.Exists(bin)) continue;
                foreach (var dir in Directory.GetDirectories(bin))
                    foreach (var pattern in new[] { "libcplex*.dylib", "libcplex*.so", "cplex*.dll" })
                        foreach (var file in Directory.GetFiles(dir, pattern).OrderDescending())
                            yield return file;
            }
        }
    }
}
