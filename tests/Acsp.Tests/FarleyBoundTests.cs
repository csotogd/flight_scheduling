using Acsp.Core;
using Acsp.Data;
using Acsp.Solver;
using Acsp.Solver.Lp;

namespace Acsp.Tests;

/// <summary>
/// Forensics for the Farley/deadline bound: after column generation converges, an extra
/// pricing pass must not claim positive reduced costs that disagree with the exact reduced
/// cost recomputed from the column's own coefficients and the LP duals. A disagreement here
/// is what inflated deadline bounds on seeded rounds.
/// </summary>
public class FarleyBoundTests
{
    /// <summary>Exact rc of a path column from its RMP coefficients (see Rmp.AddPath).</summary>
    private static double TruePathRc(Instance inst, CargoPath p, MasterDuals d)
    {
        double rc = p.Margin(inst) - d.OdDemand[p.OdId];
        var od = inst.Ods[p.OdId];
        foreach (var lid in p.LegIds)
        {
            rc -= d.LegWeight[lid];
            rc -= od.VolumePerTonne * d.LegVolume[lid];
        }
        foreach (var fid in p.LegIds.Select(l => inst.Legs[l].FlightId).Distinct())
            if (d.ImpliedBoundCuts.TryGetValue((p.OdId, fid), out double pi))
                rc -= pi;
        return rc;
    }

    /// <summary>Exact rc of a string column from its RMP coefficients (see Rmp.AddString).</summary>
    private static double TrueStringRc(Instance inst, TimelineNetwork net, FlightString s,
        MasterDuals d)
    {
        int k = s.FleetId;
        int chi = net.ChiOfString(s);
        double rc = -s.Cost(inst, withMaintenance: false) - chi * inst.Fleets[k].FixedCostPerAircraft;
        foreach (var fid in s.FlightIds)
        {
            rc -= d.FlightCover[fid];
            foreach (var lid in inst.Flights[fid].LegIds)
            {
                rc -= -inst.Fleets[k].MaxWeight * d.LegWeight[lid];
                rc -= -inst.Fleets[k].MaxVolume * d.LegVolume[lid];
            }
            foreach (var ((od, fl), pi) in d.ImpliedBoundCuts)
                if (fl == fid) rc -= -inst.Ods[od].Weight * pi;
        }
        rc -= d.DepBalance[k, s.FlightIds[0]];
        rc -= -d.ArrBalance[k, s.FlightIds[^1]];
        rc -= chi * d.FleetSize[k];
        return rc;
    }

    private static void AssertPricersAgreeAtConvergence(Instance inst)
    {
        using var rmp = new Rmp(inst, withMaintenance: false, LpSolverFactory.Create("highs"));
        rmp.SeedTrivialStrings();
        var colgen = new ColumnGeneration(inst, rmp, new ColGenOptions());
        var rest = PricingRestrictions.AllowAll(inst);
        var result = colgen.SolveNode(rest);
        Assert.Equal(LpStatus.Optimal, result.Lp.Status);

        var duals = rmp.GetDuals(result.Lp);
        var pathPricer = new PathPricer(inst);
        var stringPricer = new StringPricer(inst, withMaintenance: false, rmp.Network.CountTime);

        // invariants at convergence: (a) a pricer's claimed rc equals the exact rc from the
        // column's coefficients; (b) a column NOT in the master never has positive rc
        // (otherwise colgen terminated too early); (c) an existing column may carry positive
        // rc only when it sits at its upper bound (nonbasic-at-ub, absorbed by the bound
        // dual) — this is why the Farley bound must skip in-master columns
        foreach (var pp in pathPricer.Price(duals, rest, 1e-6))
        {
            double truth = TruePathRc(inst, pp.Path, duals);
            Assert.True(Math.Abs(pp.ReducedCost - truth) < 1e-4,
                $"path pricer rc {pp.ReducedCost:F4} != true rc {truth:F4} " +
                $"(od {pp.Path.OdId}, dup: {rmp.ContainsPath(pp.Path)})");
            Assert.True(rmp.ContainsPath(pp.Path) || truth <= 1e-4,
                $"missing path with positive rc {truth:F4}: colgen not converged");
        }
        foreach (var ps in stringPricer.Price(duals, rest, 200, 1e-6))
        {
            double truth = TrueStringRc(inst, rmp.Network, ps.Str, duals);
            Assert.True(Math.Abs(ps.ReducedCost - truth) < 1e-4,
                $"string pricer rc {ps.ReducedCost:F4} != true rc {truth:F4} " +
                $"(fleet {ps.Str.FleetId}, flights [{string.Join(',', ps.Str.FlightIds)}], " +
                $"dup: {rmp.ContainsString(ps.Str)})");
            if (rmp.ContainsString(ps.Str))
            {
                var sc = rmp.Strings.First(x => x.Str.Key() == ps.Str.Key());
                double y = result.Lp.ColumnValues[sc.Col];
                Assert.True(truth <= 1e-4 || y >= 1 - 1e-6,
                    $"existing string with positive rc {truth:F0} not at its upper bound (y={y:F6})");
            }
            else
                Assert.True(truth <= 1e-4,
                    $"missing string with positive rc {truth:F4}: colgen not converged");
        }
    }

    [Fact]
    public void Pricer_rc_matches_column_rc_at_convergence_small() =>
        AssertPricersAgreeAtConvergence(InstanceGenerator.Generate("RC", 1, 1));

    [Fact]
    public void Pricer_rc_matches_column_rc_at_convergence_with_seeded_pool()
    {
        var inst = InstanceGenerator.Generate("RC", 1, 1);
        // first solve to harvest a pool, then re-solve seeded (the design-round scenario)
        var bpc1 = new BranchAndPrice(inst, new BpcOptions
        { TimeLimitSeconds = 30, CollectColumnPool = true, LpBackend = "highs" });
        var r1 = bpc1.Solve();
        Assert.NotNull(r1.PathPool);

        using var rmp = new Rmp(inst, withMaintenance: false, LpSolverFactory.Create("highs"));
        rmp.SeedTrivialStrings();
        foreach (var p in r1.PathPool!) rmp.AddPath(p);
        foreach (var s in r1.StringPool!) rmp.AddString(s);
        var colgen = new ColumnGeneration(inst, rmp, new ColGenOptions());
        var rest = PricingRestrictions.AllowAll(inst);
        var result = colgen.SolveNode(rest);
        Assert.Equal(LpStatus.Optimal, result.Lp.Status);

        var duals = rmp.GetDuals(result.Lp);
        var stringPricer = new StringPricer(inst, withMaintenance: false, rmp.Network.CountTime);
        foreach (var ps in stringPricer.Price(duals, rest, 200, 1e-6))
        {
            double truth = TrueStringRc(inst, rmp.Network, ps.Str, duals);
            Assert.True(Math.Abs(ps.ReducedCost - truth) < 1e-4,
                $"string pricer rc {ps.ReducedCost:F4} != true rc {truth:F4} " +
                $"(dup: {rmp.ContainsString(ps.Str)})");
        }
        // and the converged DualBound must be close to the LP value, never orders above it
        Assert.True(result.DualBound <= result.Lp.Objective + Math.Abs(result.Lp.Objective),
            $"converged bound {result.DualBound:F0} wildly above LP {result.Lp.Objective:F0}");
    }
}
