using Acsp.Core;
using Acsp.Solver;
using Acsp.Solver.Lp;

namespace Acsp.Tests;

public class ColumnGenerationTests
{
    /// <summary>Copy of an instance where every flight is external with fixed leg capacities.</summary>
    private static Instance MakeAllExternal(Instance inst, double cap = 35, double vol = 220)
    {
        var legs = inst.Legs.Select(l => new Leg
        {
            Id = l.Id, FlightId = l.FlightId, Origin = l.Origin, Destination = l.Destination,
            Dep = l.Dep, Arr = l.Arr, DistanceKm = l.DistanceKm,
            VariableCostPerTonne = l.VariableCostPerTonne, TaxiMinutes = l.TaxiMinutes,
            MaxWeight = l.MaxWeight > 0 ? l.MaxWeight : cap,
            MaxVolume = l.MaxVolume > 0 ? l.MaxVolume : vol,
        }).ToArray();
        var flights = inst.Flights.Select(f => new Flight
        {
            Id = f.Id, Code = f.Code, LegIds = f.LegIds, IsExternal = true,
        }).ToArray();
        return new Instance
        {
            Name = inst.Name + "-ext", Period = inst.Period, Airports = inst.Airports,
            Fleets = inst.Fleets, Legs = legs, Flights = flights, Ods = inst.Ods,
        };
    }

    private static Instance StripOds(Instance inst) => new()
    {
        Name = inst.Name + "-noods", Period = inst.Period, Airports = inst.Airports,
        Fleets = inst.Fleets, Legs = inst.Legs, Flights = inst.Flights, Ods = [],
    };

    private static double SolveByColgen(Instance inst, bool withMaintenance, out Rmp rmp)
    {
        rmp = new Rmp(inst, withMaintenance, new HighsSolver());
        rmp.SeedTrivialStrings();
        var colgen = new ColumnGeneration(inst, rmp,
            new ColGenOptions { EnableCuts = false, ExactStringPricing = true });
        var result = colgen.SolveNode(PricingRestrictions.AllowAll(inst));
        Assert.Equal(LpStatus.Optimal, result.Lp.Status);
        return result.Lp.Objective;
    }

    private static double SolveWithAllColumns(Instance inst, bool withMaintenance)
    {
        using var rmp = new Rmp(inst, withMaintenance, new HighsSolver());
        foreach (var od in inst.Ods)
            foreach (var path in BruteForce.AllFeasiblePaths(inst, od, maxLegs: 6))
                rmp.AddPath(path);
        foreach (var s in BruteForce.AllFeasibleStrings(inst, withMaintenance, maxFlights: 5))
            rmp.AddString(s);
        var lp = rmp.SolveLp();
        Assert.Equal(LpStatus.Optimal, lp.Status);
        return lp.Objective;
    }

    [Fact]
    public void Crp_p_colgen_matches_pregenerated_lp()
    {
        // pure cargo routing: all flights external, no fleet/rotation decisions
        var inst = MakeAllExternal(TestInstances.Small());
        double all = SolveWithAllColumns(inst, withMaintenance: false);
        double colgen = SolveByColgen(inst, withMaintenance: false, out var rmp);
        using (rmp) Assert.Equal(all, colgen, 4);
        Assert.True(colgen > 0, "routing on free external capacity should be profitable");
    }

    [Fact]
    public void Farp_colgen_matches_pregenerated_lp()
    {
        // pure fleeting/rotation: no demand, all flights mandatory -> minimize cost
        var inst = StripOds(TestInstances.Small(allMandatory: true));
        foreach (bool mnt in new[] { false, true })
        {
            double all = SolveWithAllColumns(inst, mnt);
            double colgen = SolveByColgen(inst, mnt, out var rmp);
            using (rmp) Assert.Equal(all, colgen, 4);
            Assert.True(colgen < 0, "covering mandatory flights must cost money");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Integrated_root_lp_matches_pregenerated_lp(bool withMaintenance)
    {
        var inst = TestInstances.Small();
        double all = SolveWithAllColumns(inst, withMaintenance);
        double colgen = SolveByColgen(inst, withMaintenance, out var rmp);
        using (rmp) Assert.Equal(all, colgen, 4);
    }

    [Fact]
    public void Integrated_root_lp_on_tiny()
    {
        var inst = TestInstances.Tiny();
        double all = SolveWithAllColumns(inst, withMaintenance: false);
        double colgen = SolveByColgen(inst, withMaintenance: false, out var rmp);
        using (rmp) Assert.Equal(all, colgen, 4);
    }

    [Fact]
    public void Cuts_tighten_the_relaxation_and_mip_on_columns_is_feasible()
    {
        var inst = TestInstances.Small();
        using var rmp = new Rmp(inst, withMaintenance: false, new HighsSolver());
        rmp.SeedTrivialStrings();
        var colgenNoCuts = new ColumnGeneration(inst, rmp, new ColGenOptions { EnableCuts = false });
        var rest = PricingRestrictions.AllowAll(inst);
        var noCuts = colgenNoCuts.SolveNode(rest);

        var colgenCuts = new ColumnGeneration(inst, rmp, new ColGenOptions { EnableCuts = true });
        var withCuts = colgenCuts.SolveNode(rest);
        Assert.True(withCuts.Lp.Objective <= noCuts.Lp.Objective + 1e-6,
            "cuts may only tighten the relaxation");

        // integer solve on the generated columns yields a feasible schedule
        var mip = rmp.SolveMipOnCurrentColumns(30);
        Assert.Equal(LpStatus.Optimal, mip.Status);
        var sol = rmp.ExtractSolution(mip);
        SolutionAssembler.AssembleRotations(inst, sol);
        var report = FeasibilityChecker.Check(inst, sol);
        Assert.True(report.IsFeasible, report.ToString());
        Assert.True(mip.Objective <= withCuts.Lp.Objective + 1e-6, "MIP cannot beat the LP bound");
    }

    [Fact]
    public void Assembler_builds_valid_rotations_for_multi_string_solution()
    {
        var inst = TestInstances.Small(allMandatory: true);
        using var rmp = new Rmp(inst, withMaintenance: true, new HighsSolver());
        rmp.SeedTrivialStrings();
        var colgen = new ColumnGeneration(inst, rmp, new ColGenOptions { ExactStringPricing = true });
        colgen.SolveNode(PricingRestrictions.AllowAll(inst));
        var mip = rmp.SolveMipOnCurrentColumns(30);
        Assert.Equal(LpStatus.Optimal, mip.Status);
        var sol = rmp.ExtractSolution(mip);
        SolutionAssembler.AssembleRotations(inst, sol);
        Assert.NotEmpty(sol.Rotations);
        var report = FeasibilityChecker.Check(inst, sol);
        Assert.True(report.IsFeasible, report.ToString());
    }
}
