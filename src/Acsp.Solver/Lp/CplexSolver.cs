namespace Acsp.Solver.Lp;

/// <summary>
/// Placeholder for the CPLEX backend. When a local IBM CPLEX Studio installation is present
/// (detected under /Applications/CPLEX_Studio*), this class will wrap the CPLEX .NET/C API
/// behind ILpSolver. Until then, construction fails with a helpful message and the HiGHS
/// backend should be used.
/// </summary>
public static class CplexSolver
{
    public static ILpSolver CreateOrThrow()
    {
        var candidates = Directory.Exists("/Applications")
            ? Directory.GetDirectories("/Applications", "CPLEX_Studio*")
            : [];
        throw new NotSupportedException(candidates.Length == 0
            ? "CPLEX is not installed (no /Applications/CPLEX_Studio* found). Use the 'highs' backend."
            : $"CPLEX found at {candidates[0]} but the CplexSolver backend is not wired up yet. " +
              "Use the 'highs' backend for now.");
    }
}
