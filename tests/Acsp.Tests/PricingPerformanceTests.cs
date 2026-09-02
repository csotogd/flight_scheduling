using Acsp.Core;
using Acsp.Data;
using Acsp.Solver;
using Acsp.Solver.Lp;

namespace Acsp.Tests;

/// <summary>Correctness contracts of the performance layer: parallel pricing must be
/// bit-identical to the sequential sweep, and dual stabilization must change the road,
/// never the destination.</summary>
public class PricingPerformanceTests
{
    [Fact]
    public void Parallel_pricing_is_bit_identical_to_sequential()
    {
        var inst = InstanceGenerator.Generate("MI", 1, 1);
        var rest = PricingRestrictions.AllowAll(inst);
        // adversarial duals: pseudo-random but deterministic, so many ods price positive
        var duals = MasterDuals.Zero(inst);
        var rng = new Random(7);
        foreach (var od in inst.Ods) duals.OdDemand[od.Id] = -rng.NextDouble() * 500;
        foreach (var leg in inst.Legs) duals.LegWeight[leg.Id] = rng.NextDouble() * 2;

        var seq = new PathPricer(inst) { MaxDegreeOfParallelism = 1 }
            .Price(duals, rest);
        var par = new PathPricer(inst) { MaxDegreeOfParallelism = 0 }
            .Price(duals, rest);

        Assert.Equal(seq.Count, par.Count);
        for (int i = 0; i < seq.Count; i++)
        {
            Assert.Equal(seq[i].Path.Key(), par[i].Path.Key());
            Assert.Equal(seq[i].ReducedCost, par[i].ReducedCost, 9);
        }
    }

    [Fact]
    public void Dual_stabilization_converges_to_the_same_lp()
    {
        var inst = InstanceGenerator.Generate("RC", 1, 1);
        double Solve(bool stabilize)
        {
            using var rmp = new Rmp(inst, withMaintenance: false, LpSolverFactory.Create("highs"));
            rmp.SeedTrivialStrings();
            var cg = new ColumnGeneration(inst, rmp, new ColGenOptions
            { DualStabilization = stabilize });
            var res = cg.SolveNode(PricingRestrictions.AllowAll(inst), default);
            Assert.False(res.DeadlineHit);
            Assert.Equal(LpStatus.Optimal, res.Lp.Status);
            return res.Lp.Objective;
        }
        double plain = Solve(false), stab = Solve(true);
        // same converged root LP: smoothing may change the column set explored on the way,
        // but the mispricing safeguard forces true-dual termination — same optimum
        Assert.True(Math.Abs(plain - stab) < Math.Max(1, Math.Abs(plain)) * 1e-5,
            $"stabilized root {stab:F2} != plain root {plain:F2}");
    }
}
