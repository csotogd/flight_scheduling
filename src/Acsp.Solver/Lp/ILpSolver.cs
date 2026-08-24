namespace Acsp.Solver.Lp;

public enum LpStatus { Optimal, Infeasible, Unbounded, TimeLimit, Other }

/// <summary>
/// Result of an LP or MIP solve. All values are expressed for the MAXIMIZATION problem as
/// stated through the interface. Duals follow the convention: for rows in &lt;= form the dual
/// is &gt;= 0 and the reduced cost of a column is obj - sum_r a_r * pi_r. Duals are empty for
/// MIP solves.
/// </summary>
public sealed record LpResult(
    LpStatus Status, double Objective, double[] ColumnValues, double[] RowDuals, double DualBound);

/// <summary>
/// Thin abstraction over an LP/MIP solver used by the restricted master problem. The model is
/// always a maximization. Implementations: HighsSolver (default, open source) and CplexSolver
/// (optional, when a local CPLEX installation is available).
/// </summary>
public interface ILpSolver : IDisposable
{
    int NumColumns { get; }
    int NumRows { get; }

    /// <summary>Adds a column with the given objective coefficient, bounds and row entries.</summary>
    int AddColumn(double objective, double lower, double upper,
        ReadOnlySpan<int> rows, ReadOnlySpan<double> coefs);

    /// <summary>Adds a row with bounds lower &lt;= a x &lt;= upper (use +-inf for one-sided rows).</summary>
    int AddRow(double lower, double upper, ReadOnlySpan<int> cols, ReadOnlySpan<double> coefs);

    void SetColumnBounds(int col, double lower, double upper);
    void SetRowBounds(int row, double lower, double upper);
    /// <summary>Marks a column as integer for MIP solves; LP solves always relax integrality.</summary>
    void SetInteger(int col, bool isInteger);

    LpResult SolveLp();
    /// <param name="mipStart">Optional sparse warm-start values (column, value) for the MIP;
    /// unspecified integer columns may be completed by the backend. Backends without partial
    /// MIP-start support are free to ignore it.</param>
    LpResult SolveMip(double timeLimitSeconds = double.PositiveInfinity, double mipGap = 1e-6,
        IReadOnlyList<(int Col, double Value)>? mipStart = null);
}

public static class LpSolverFactory
{
    /// <summary>
    /// Creates the configured solver backend: "highs", "cplex", or "auto" (the default), which
    /// picks CPLEX when a local installation is found and falls back to HiGHS otherwise.
    /// </summary>
    public static ILpSolver Create(string? backend = null)
    {
        backend ??= Environment.GetEnvironmentVariable("ACSP_LP_BACKEND") ?? "auto";
        return backend.ToLowerInvariant() switch
        {
            "highs" => new HighsSolver(),
            "cplex" => CplexSolver.CreateOrThrow(),
            "auto" => CplexSolver.IsAvailable ? new CplexSolver() : new HighsSolver(),
            _ => throw new ArgumentException($"Unknown LP backend '{backend}'"),
        };
    }

    /// <summary>Name of the backend that Create(null) will pick, for logging.</summary>
    public static string DefaultBackendName =>
        (Environment.GetEnvironmentVariable("ACSP_LP_BACKEND") ?? "auto") switch
        {
            "auto" => CplexSolver.IsAvailable ? "cplex" : "highs",
            var b => b,
        };
}
