using System.Runtime.InteropServices;

namespace Acsp.Solver.Lp;

/// <summary>
/// ILpSolver backend over the HiGHS C API (libhighs). The interface exposes a maximization
/// problem; internally the objective is negated and solved as a minimization so that dual
/// signs are unambiguous: for min(-c)x with Ax &lt;= b HiGHS returns row duals lambda &lt;= 0,
/// and pi = -lambda &gt;= 0 matches the maximization convention of LpResult.
/// </summary>
public sealed class HighsSolver : ILpSolver
{
    private IntPtr _h;
    private readonly List<bool> _integer = [];
    private bool _integralityDirty;

    public HighsSolver()
    {
        _h = Native.Highs_create();
        Check(Native.Highs_setBoolOptionValue(_h, "output_flag", 0), "output_flag");
        Native.Highs_changeObjectiveSense(_h, 1); // minimize (we negate the objective)
    }

    public int NumColumns => Native.Highs_getNumCol(_h);
    public int NumRows => Native.Highs_getNumRow(_h);

    public int AddColumn(double objective, double lower, double upper,
        ReadOnlySpan<int> rows, ReadOnlySpan<double> coefs)
    {
        Check(Native.Highs_addCol(_h, -objective, lower, upper, rows.Length,
            ref MemoryMarshal.GetReference(rows), ref MemoryMarshal.GetReference(coefs)), "addCol");
        _integer.Add(false);
        return NumColumns - 1;
    }

    public int AddRow(double lower, double upper, ReadOnlySpan<int> cols, ReadOnlySpan<double> coefs)
    {
        Check(Native.Highs_addRow(_h, lower, upper, cols.Length,
            ref MemoryMarshal.GetReference(cols), ref MemoryMarshal.GetReference(coefs)), "addRow");
        return NumRows - 1;
    }

    public void SetColumnBounds(int col, double lower, double upper) =>
        Check(Native.Highs_changeColBounds(_h, col, lower, upper), "changeColBounds");

    public void SetRowBounds(int row, double lower, double upper)
    {
        Span<int> set = [row];
        Span<double> lo = [lower];
        Span<double> hi = [upper];
        Check(Native.Highs_changeRowsBoundsBySet(_h, 1,
            ref MemoryMarshal.GetReference(set),
            ref MemoryMarshal.GetReference(lo),
            ref MemoryMarshal.GetReference(hi)), "changeRowsBoundsBySet");
    }

    public void SetInteger(int col, bool isInteger)
    {
        if (_integer[col] == isInteger) return;
        _integer[col] = isInteger;
        _integralityDirty = true;
    }

    public LpResult SolveLp()
    {
        ApplyIntegrality(relax: true);
        Native.Highs_setDoubleOptionValue(_h, "time_limit", 1e30);
        Check(Native.Highs_run(_h), "run");
        return Extract(withDuals: true);
    }

    public LpResult SolveMip(double timeLimitSeconds = double.PositiveInfinity, double mipGap = 1e-6)
    {
        ApplyIntegrality(relax: false);
        Native.Highs_setDoubleOptionValue(_h, "time_limit",
            double.IsFinite(timeLimitSeconds) ? timeLimitSeconds : 1e30);
        Native.Highs_setDoubleOptionValue(_h, "mip_rel_gap", mipGap);
        Check(Native.Highs_run(_h), "run(mip)");
        var res = Extract(withDuals: false);
        // restore continuous marks lazily on the next LP solve
        _integralityDirty = true;
        return res;
    }

    private void ApplyIntegrality(bool relax)
    {
        int n = _integer.Count;
        if (n == 0) return;
        if (relax && !_integralityDirty) return;
        var set = new int[n];
        var kinds = new int[n];
        for (int i = 0; i < n; i++)
        {
            set[i] = i;
            kinds[i] = !relax && _integer[i] ? 1 : 0; // 1 = kHighsVarTypeInteger
        }
        Check(Native.Highs_changeColsIntegralityBySet(_h, n, set, kinds), "integrality");
        _integralityDirty = !relax;
    }

    private LpResult Extract(bool withDuals)
    {
        int status = Native.Highs_getModelStatus(_h);
        var lpStatus = status switch
        {
            7 => LpStatus.Optimal,        // kHighsModelStatusOptimal
            8 => LpStatus.Infeasible,     // kHighsModelStatusInfeasible
            9 or 10 => LpStatus.Unbounded,
            13 => LpStatus.TimeLimit,
            _ => LpStatus.Other,
        };
        int nCol = NumColumns, nRow = NumRows;
        var colValues = new double[nCol];
        var rowDuals = new double[nRow];
        if (lpStatus is LpStatus.Optimal or LpStatus.TimeLimit)
        {
            var colDual = new double[nCol];
            var rowValue = new double[nRow];
            Native.Highs_getSolution(_h, colValues, colDual, rowValue, rowDuals);
            if (withDuals)
                for (int i = 0; i < nRow; i++) rowDuals[i] = -rowDuals[i]; // min->max convention
            else
                Array.Clear(rowDuals);
        }
        double obj = -Native.Highs_getObjectiveValue(_h);
        double dualBound = obj;
        if (!withDuals && Native.Highs_getDoubleInfoValue(_h, "mip_dual_bound", out double db) == 0)
            dualBound = -db;
        return new LpResult(lpStatus, obj, colValues, rowDuals, dualBound);
    }

    private static void Check(int status, string op)
    {
        // kHighsStatusOk = 0, kHighsStatusWarning = 1
        if (status != 0 && status != 1)
            throw new InvalidOperationException($"HiGHS {op} failed with status {status}");
    }

    public void Dispose()
    {
        if (_h != IntPtr.Zero) { Native.Highs_destroy(_h); _h = IntPtr.Zero; }
    }

    private static class Native
    {
        private const string Lib = "highs";

        static Native()
        {
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) =>
            {
                if (name != Lib) return IntPtr.Zero;
                foreach (var candidate in new[]
                {
                    Environment.GetEnvironmentVariable("ACSP_LIBHIGHS") ?? "",
                    "/opt/homebrew/lib/libhighs.dylib",
                    "/usr/local/lib/libhighs.dylib",
                    "libhighs.so", "libhighs.dylib", "highs.dll",
                })
                {
                    if (candidate.Length > 0 && NativeLibrary.TryLoad(candidate, out var handle))
                        return handle;
                }
                return IntPtr.Zero;
            });
        }

        [DllImport(Lib)] public static extern IntPtr Highs_create();
        [DllImport(Lib)] public static extern void Highs_destroy(IntPtr h);
        [DllImport(Lib)] public static extern int Highs_run(IntPtr h);
        [DllImport(Lib)] public static extern int Highs_getModelStatus(IntPtr h);
        [DllImport(Lib)] public static extern double Highs_getObjectiveValue(IntPtr h);
        [DllImport(Lib)] public static extern int Highs_getNumCol(IntPtr h);
        [DllImport(Lib)] public static extern int Highs_getNumRow(IntPtr h);
        [DllImport(Lib)] public static extern int Highs_changeObjectiveSense(IntPtr h, int sense);
        [DllImport(Lib)] public static extern int Highs_addCol(IntPtr h, double cost, double lower,
            double upper, int nnz, ref int index, ref double value);
        [DllImport(Lib)] public static extern int Highs_addRow(IntPtr h, double lower, double upper,
            int nnz, ref int index, ref double value);
        [DllImport(Lib)] public static extern int Highs_changeColBounds(IntPtr h, int col,
            double lower, double upper);
        [DllImport(Lib)] public static extern int Highs_changeRowsBoundsBySet(IntPtr h, int n,
            ref int set, ref double lower, ref double upper);
        [DllImport(Lib)] public static extern int Highs_changeColsIntegralityBySet(IntPtr h, int n,
            int[] set, int[] integrality);
        [DllImport(Lib, CharSet = CharSet.Ansi)] public static extern int Highs_setBoolOptionValue(
            IntPtr h, string option, int value);
        [DllImport(Lib, CharSet = CharSet.Ansi)] public static extern int Highs_setDoubleOptionValue(
            IntPtr h, string option, double value);
        [DllImport(Lib)] public static extern int Highs_getSolution(IntPtr h,
            double[] colValue, double[] colDual, double[] rowValue, double[] rowDual);
        [DllImport(Lib, CharSet = CharSet.Ansi)] public static extern int Highs_getDoubleInfoValue(
            IntPtr h, string info, out double value);
    }
}
