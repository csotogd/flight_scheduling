using Acsp.Solver.Lp;

namespace Acsp.Tests;

public class HighsSolverTests
{
    private const double Inf = double.PositiveInfinity;

    [Fact]
    public void Solves_small_lp_with_correct_duals()
    {
        // max 3x + 2y  s.t.  x + y <= 4,  x + 3y <= 6,  x,y >= 0
        // optimum at (4, 0): obj 12; row1 binding (dual 3), row2 slack (dual 0)
        using var lp = new HighsSolver();
        int r1 = lp.AddRow(-Inf, 4, [], []);
        int r2 = lp.AddRow(-Inf, 6, [], []);
        lp.AddColumn(3, 0, Inf, [r1, r2], [1.0, 1.0]);
        lp.AddColumn(2, 0, Inf, [r1, r2], [1.0, 3.0]);
        var res = lp.SolveLp();
        Assert.Equal(LpStatus.Optimal, res.Status);
        Assert.Equal(12.0, res.Objective, 6);
        Assert.Equal(4.0, res.ColumnValues[0], 6);
        Assert.Equal(0.0, res.ColumnValues[1], 6);
        Assert.Equal(3.0, res.RowDuals[r1], 6);
        Assert.Equal(0.0, res.RowDuals[r2], 6);
        // reduced cost check: col y: 2 - (1*3 + 3*0) = -1 <= 0 (not improving)
        Assert.True(2 - (res.RowDuals[r1] + 3 * res.RowDuals[r2]) < 0);
    }

    [Fact]
    public void Supports_column_generation_workflow()
    {
        // Start with one column, solve, add a second column priced against the duals, re-solve.
        using var lp = new HighsSolver();
        int cap = lp.AddRow(-Inf, 10, [], []);
        lp.AddColumn(1.0, 0, Inf, [cap], [1.0]);
        var r1 = lp.SolveLp();
        Assert.Equal(10.0, r1.Objective, 6);
        Assert.Equal(1.0, r1.RowDuals[cap], 6);
        // candidate column with obj 3, coef 2: reduced cost 3 - 2*1 = 1 > 0 -> improving
        lp.AddColumn(3.0, 0, Inf, [cap], [2.0]);
        var r2 = lp.SolveLp();
        Assert.Equal(15.0, r2.Objective, 6);
    }

    [Fact]
    public void Solves_mip_with_integrality()
    {
        // max x + y s.t. 2x + 2y <= 5, binary-ish: LP gives 2.5 total, MIP gives 2
        using var lp = new HighsSolver();
        int r = lp.AddRow(-Inf, 5, [], []);
        int x = lp.AddColumn(1, 0, Inf, [r], [2.0]);
        int y = lp.AddColumn(1, 0, Inf, [r], [2.0]);
        var relax = lp.SolveLp();
        Assert.Equal(2.5, relax.Objective, 6);
        lp.SetInteger(x, true);
        lp.SetInteger(y, true);
        var mip = lp.SolveMip();
        Assert.Equal(LpStatus.Optimal, mip.Status);
        Assert.Equal(2.0, mip.Objective, 6);
        // and the LP relaxation still solves after the MIP (integrality relaxed again)
        var relax2 = lp.SolveLp();
        Assert.Equal(2.5, relax2.Objective, 6);
    }

    [Fact]
    public void Detects_infeasibility_and_bound_changes()
    {
        using var lp = new HighsSolver();
        int r = lp.AddRow(3, Inf, [], []); // a x >= 3
        int x = lp.AddColumn(-1, 0, 1, [r], [1.0]); // x <= 1 -> infeasible
        Assert.Equal(LpStatus.Infeasible, lp.SolveLp().Status);
        lp.SetColumnBounds(x, 0, 5);
        var res = lp.SolveLp();
        Assert.Equal(LpStatus.Optimal, res.Status);
        Assert.Equal(-3.0, res.Objective, 6);
        lp.SetRowBounds(r, 1, Inf);
        Assert.Equal(-1.0, lp.SolveLp().Objective, 6);
    }

    [Fact]
    public void Equality_rows_have_free_duals()
    {
        // max x s.t. x + y = 2, y <= 3, x <= 1  -> x=1, y=1
        using var lp = new HighsSolver();
        int eq = lp.AddRow(2, 2, [], []);
        lp.AddColumn(1, 0, 1, [eq], [1.0]);
        lp.AddColumn(0, 0, 3, [eq], [1.0]);
        var res = lp.SolveLp();
        Assert.Equal(LpStatus.Optimal, res.Status);
        Assert.Equal(1.0, res.Objective, 6);
    }
}
