using System.Runtime.InteropServices;

namespace Acsp.Solver.Lp;

/// <summary>
/// ILpSolver backend over the IBM CPLEX C API (libcplex). The model is solved directly as a
/// maximization (CPX_MAX), so duals of &lt;= rows are &gt;= 0 and reduced costs follow
/// obj - sum_r a_r * pi_r, matching the LpResult convention without sign flips. The library is
/// located at runtime under CPLEX_Studio* (see Native.Candidates) or via ACSP_LIBCPLEX; use
/// <see cref="IsAvailable"/> to test for a usable installation before constructing.
/// </summary>
public sealed class CplexSolver : ILpSolver
{
    private const double Inf = 1e20; // CPX_INFBOUND

    private IntPtr _env;
    private IntPtr _lp;
    private readonly List<bool> _integer = [];
    private bool _isMip; // problem type currently CPXPROB_MILP

    public static bool IsAvailable => Native.Handle.Value != IntPtr.Zero;

    public static ILpSolver CreateOrThrow()
    {
        if (!IsAvailable)
            throw new NotSupportedException(
                "CPLEX shared library not found (searched CPLEX_Studio* under /Applications, " +
                "~/Applications and /opt/ibm/ILOG; set ACSP_LIBCPLEX to override). " +
                "Use the 'highs' backend.");
        return new CplexSolver();
    }

    public CplexSolver()
    {
        _env = Native.CPXopenCPLEX(out int status);
        if (_env == IntPtr.Zero) throw new InvalidOperationException($"CPXopenCPLEX failed ({status})");
        Check(Native.CPXsetintparam(_env, 1035, 0), "SCRIND off"); // CPX_PARAM_SCRIND
        _lp = Native.CPXcreateprob(_env, out status, "acsp");
        if (_lp == IntPtr.Zero) throw new InvalidOperationException($"CPXcreateprob failed ({status})");
        Check(Native.CPXchgobjsen(_env, _lp, -1), "chgobjsen"); // CPX_MAX
    }

    public int NumColumns => Native.CPXgetnumcols(_env, _lp);
    public int NumRows => Native.CPXgetnumrows(_env, _lp);

    public int AddColumn(double objective, double lower, double upper,
        ReadOnlySpan<int> rows, ReadOnlySpan<double> coefs)
    {
        Check(Native.CPXaddcols(_env, _lp, 1, rows.Length, [objective], [0],
            rows.ToArray(), coefs.ToArray(), [Clamp(lower)], [Clamp(upper)], IntPtr.Zero), "addcols");
        _integer.Add(false);
        return NumColumns - 1;
    }

    public int AddRow(double lower, double upper, ReadOnlySpan<int> cols, ReadOnlySpan<double> coefs)
    {
        var (sense, rhs, range) = RowForm(lower, upper);
        Check(Native.CPXaddrows(_env, _lp, 0, 1, cols.Length, [rhs], [(byte)sense], [0],
            cols.ToArray(), coefs.ToArray(), IntPtr.Zero, IntPtr.Zero), "addrows");
        int row = NumRows - 1;
        if (sense == 'R')
            Check(Native.CPXchgrngval(_env, _lp, 1, [row], [range]), "chgrngval");
        return row;
    }

    public void SetColumnBounds(int col, double lower, double upper) =>
        Check(Native.CPXchgbds(_env, _lp, 2, [col, col], [(byte)'L', (byte)'U'],
            [Clamp(lower), Clamp(upper)]), "chgbds");

    public void SetRowBounds(int row, double lower, double upper)
    {
        var (sense, rhs, range) = RowForm(lower, upper);
        Check(Native.CPXchgsense(_env, _lp, 1, [row], [(byte)sense]), "chgsense");
        Check(Native.CPXchgrhs(_env, _lp, 1, [row], [rhs]), "chgrhs");
        if (sense == 'R')
            Check(Native.CPXchgrngval(_env, _lp, 1, [row], [range]), "chgrngval");
    }

    public void SetInteger(int col, bool isInteger) => _integer[col] = isInteger;

    public LpResult SolveLp()
    {
        if (_isMip)
        {
            Check(Native.CPXchgprobtype(_env, _lp, 0), "chgprobtype(LP)"); // drops ctypes
            _isMip = false;
        }
        Check(Native.CPXlpopt(_env, _lp), "lpopt");
        int stat = Native.CPXgetstat(_env, _lp);
        var status = stat switch
        {
            1 => LpStatus.Optimal,       // CPX_STAT_OPTIMAL
            2 => LpStatus.Unbounded,
            3 => LpStatus.Infeasible,
            11 => LpStatus.TimeLimit,    // CPX_STAT_ABORT_TIME_LIM
            _ => LpStatus.Other,
        };
        return Extract(status, withDuals: true);
    }

    public LpResult SolveMip(double timeLimitSeconds = double.PositiveInfinity, double mipGap = 1e-6,
        IReadOnlyList<(int Col, double Value)>? mipStart = null)
    {
        int n = NumColumns;
        if (n > 0)
        {
            if (!_isMip)
            {
                Check(Native.CPXchgprobtype(_env, _lp, 1), "chgprobtype(MILP)");
                _isMip = true;
            }
            var idx = new int[n];
            var ctype = new byte[n];
            for (int i = 0; i < n; i++)
            {
                idx[i] = i;
                ctype[i] = (byte)(_integer[i] ? 'I' : 'C');
            }
            Check(Native.CPXchgctype(_env, _lp, n, idx, ctype), "chgctype");
        }
        if (mipStart is { Count: > 0 })
        {
            var idx = new int[mipStart.Count];
            var val = new double[mipStart.Count];
            for (int i = 0; i < mipStart.Count; i++)
                (idx[i], val[i]) = mipStart[i];
            // effort 2 = CPX_MIPSTART_SOLVEFIXED: fix the given variables, complete the rest
            // (here: continuous ground arcs) by solving the remaining LP
            Check(Native.CPXaddmipstarts(_env, _lp, 1, mipStart.Count, [0], idx, val,
                [2], IntPtr.Zero), "addmipstarts");
        }
        Check(Native.CPXsetdblparam(_env, 1039, // CPX_PARAM_TILIM
            double.IsFinite(timeLimitSeconds) ? timeLimitSeconds : 1e75), "TILIM");
        Check(Native.CPXsetdblparam(_env, 2009, mipGap), "EPGAP"); // CPX_PARAM_EPGAP
        Check(Native.CPXmipopt(_env, _lp), "mipopt");
        Check(Native.CPXsetdblparam(_env, 1039, 1e75), "TILIM reset");
        int stat = Native.CPXgetstat(_env, _lp);
        var status = stat switch
        {
            101 or 102 => LpStatus.Optimal,  // CPXMIP_OPTIMAL(_TOL)
            103 => LpStatus.Infeasible,
            118 => LpStatus.Unbounded,
            107 or 108 => LpStatus.TimeLimit, // CPXMIP_TIME_LIM_(IN)FEAS
            _ => LpStatus.Other,
        };
        return Extract(status, withDuals: false);
    }

    private LpResult Extract(LpStatus status, bool withDuals)
    {
        int nCol = NumColumns, nRow = NumRows;
        var colValues = new double[nCol];
        var rowDuals = new double[nRow];
        Native.CPXsolninfo(_env, _lp, out _, out int solnType, out _, out _);
        bool hasSolution = solnType != 0; // CPX_NO_SOLN
        double obj = double.NegativeInfinity;
        if (hasSolution)
        {
            Check(Native.CPXgetobjval(_env, _lp, out obj), "getobjval");
            if (nCol > 0) Check(Native.CPXgetx(_env, _lp, colValues, 0, nCol - 1), "getx");
            if (withDuals && nRow > 0) Check(Native.CPXgetpi(_env, _lp, rowDuals, 0, nRow - 1), "getpi");
        }
        double dualBound = obj;
        if (!withDuals && Native.CPXgetbestobjval(_env, _lp, out double bb) == 0)
            dualBound = bb;
        return new LpResult(status, obj, colValues, rowDuals, dualBound);
    }

    private static (char Sense, double Rhs, double Range) RowForm(double lower, double upper) =>
        lower == upper ? ('E', lower, 0)
        : double.IsNegativeInfinity(lower) || lower <= -Inf ? ('L', upper, 0)
        : double.IsPositiveInfinity(upper) || upper >= Inf ? ('G', lower, 0)
        : ('R', lower, upper - lower); // rhs <= a x <= rhs + range

    private static double Clamp(double b) => double.IsNegativeInfinity(b) ? -Inf
        : double.IsPositiveInfinity(b) ? Inf : b;

    private void Check(int status, string op)
    {
        if (status != 0)
            throw new InvalidOperationException($"CPLEX {op} failed with status {status}");
    }

    public void Dispose()
    {
        if (_lp != IntPtr.Zero) { Native.CPXfreeprob(_env, ref _lp); _lp = IntPtr.Zero; }
        if (_env != IntPtr.Zero) { Native.CPXcloseCPLEX(ref _env); _env = IntPtr.Zero; }
    }

    private static class Native
    {
        private const string Lib = "cplex";

        public static readonly Lazy<IntPtr> Handle = NativeLoader.Cplex;

        static Native() => NativeLoader.EnsureRegistered();

        [DllImport(Lib)] public static extern IntPtr CPXopenCPLEX(out int status);
        [DllImport(Lib)] public static extern int CPXcloseCPLEX(ref IntPtr env);
        [DllImport(Lib, CharSet = CharSet.Ansi)] public static extern IntPtr CPXcreateprob(
            IntPtr env, out int status, string name);
        [DllImport(Lib)] public static extern int CPXfreeprob(IntPtr env, ref IntPtr lp);
        [DllImport(Lib)] public static extern int CPXchgobjsen(IntPtr env, IntPtr lp, int maxormin);
        [DllImport(Lib)] public static extern int CPXaddcols(IntPtr env, IntPtr lp, int ccnt,
            int nzcnt, double[] obj, int[] cmatbeg, int[] cmatind, double[] cmatval,
            double[] lb, double[] ub, IntPtr colname);
        [DllImport(Lib)] public static extern int CPXaddrows(IntPtr env, IntPtr lp, int ccnt,
            int rcnt, int nzcnt, double[] rhs, byte[] sense, int[] rmatbeg, int[] rmatind,
            double[] rmatval, IntPtr colname, IntPtr rowname);
        [DllImport(Lib)] public static extern int CPXchgbds(IntPtr env, IntPtr lp, int cnt,
            int[] indices, byte[] lu, double[] bd);
        [DllImport(Lib)] public static extern int CPXchgsense(IntPtr env, IntPtr lp, int cnt,
            int[] indices, byte[] sense);
        [DllImport(Lib)] public static extern int CPXchgrhs(IntPtr env, IntPtr lp, int cnt,
            int[] indices, double[] values);
        [DllImport(Lib)] public static extern int CPXchgrngval(IntPtr env, IntPtr lp, int cnt,
            int[] indices, double[] values);
        [DllImport(Lib)] public static extern int CPXchgctype(IntPtr env, IntPtr lp, int cnt,
            int[] indices, byte[] ctype);
        [DllImport(Lib)] public static extern int CPXaddmipstarts(IntPtr env, IntPtr lp,
            int mcnt, int nzcnt, int[] beg, int[] varindices, double[] values,
            int[] effortlevel, IntPtr mipstartname);
        [DllImport(Lib)] public static extern int CPXchgprobtype(IntPtr env, IntPtr lp, int type);
        [DllImport(Lib)] public static extern int CPXlpopt(IntPtr env, IntPtr lp);
        [DllImport(Lib)] public static extern int CPXmipopt(IntPtr env, IntPtr lp);
        [DllImport(Lib)] public static extern int CPXgetstat(IntPtr env, IntPtr lp);
        [DllImport(Lib)] public static extern int CPXgetobjval(IntPtr env, IntPtr lp, out double obj);
        [DllImport(Lib)] public static extern int CPXgetbestobjval(IntPtr env, IntPtr lp, out double obj);
        [DllImport(Lib)] public static extern int CPXgetx(IntPtr env, IntPtr lp, double[] x,
            int begin, int end);
        [DllImport(Lib)] public static extern int CPXgetpi(IntPtr env, IntPtr lp, double[] pi,
            int begin, int end);
        [DllImport(Lib)] public static extern int CPXsolninfo(IntPtr env, IntPtr lp,
            out int method, out int type, out int primalFeasible, out int dualFeasible);
        [DllImport(Lib)] public static extern int CPXgetnumcols(IntPtr env, IntPtr lp);
        [DllImport(Lib)] public static extern int CPXgetnumrows(IntPtr env, IntPtr lp);
        [DllImport(Lib)] public static extern int CPXsetintparam(IntPtr env, int param, int value);
        [DllImport(Lib)] public static extern int CPXsetdblparam(IntPtr env, int param, double value);
    }
}
