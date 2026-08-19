using Acsp.Core;
using Acsp.Data;
using Acsp.Solver;
using Acsp.Solver.Lp;

namespace Acsp.Tests;

public class BranchAndPriceTests
{
    private static BpcResult RunBpc(Instance inst, bool withMaintenance, bool exactStrings = false)
    {
        var bpc = new BranchAndPrice(inst, new BpcOptions
        {
            WithMaintenance = withMaintenance,
            GapTarget = 1e-6, // solve to optimality for the comparisons
            TimeLimitSeconds = 300,
            ColGen = new ColGenOptions { ExactStringPricing = exactStrings },
        });
        return bpc.Solve();
    }

    private static void AssertMatchesDirectMip(Instance inst, bool withMaintenance)
    {
        var paths = DirectMipSolver.EnumeratePaths(inst, maxLegs: 5).ToList();
        var strings = withMaintenance
            ? BruteForce.AllFeasibleStrings(inst, withMaintenance: true, maxFlights: 5)
            : null;
        var direct = DirectMipSolver.Solve(inst, withMaintenance, paths, strings);
        Assert.Equal(LpStatus.Optimal, direct.Status);
        Assert.NotNull(direct.Solution);
        var directReport = FeasibilityChecker.Check(inst, direct.Solution!);
        Assert.True(directReport.IsFeasible, directReport.ToString());

        var bpc = RunBpc(inst, withMaintenance, exactStrings: withMaintenance);
        Assert.NotNull(bpc.Best);
        var report = FeasibilityChecker.Check(inst, bpc.Best!);
        Assert.True(report.IsFeasible, report.ToString());
        Assert.Equal(direct.Objective, bpc.Objective, 3);
    }

    [Fact]
    public void Matches_direct_mip_on_tiny() =>
        AssertMatchesDirectMip(TestInstances.Tiny(), withMaintenance: false);

    [Fact]
    public void Matches_direct_mip_on_tiny_all_mandatory() =>
        AssertMatchesDirectMip(TestInstances.Tiny(f1Mandatory: true), withMaintenance: false);

    [Fact]
    public void Matches_direct_mip_on_small() =>
        AssertMatchesDirectMip(TestInstances.Small(), withMaintenance: false);

    [Fact]
    public void Matches_direct_mip_on_small_all_mandatory() =>
        AssertMatchesDirectMip(TestInstances.Small(allMandatory: true), withMaintenance: false);

    [Fact]
    public void Matches_direct_mip_on_small_with_maintenance() =>
        AssertMatchesDirectMip(TestInstances.Small(), withMaintenance: true);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Matches_direct_mip_on_generated_mini_instances(int seed)
    {
        // a scaled-down airline so that all paths can be enumerated
        var profile = AirlineProfile.RC with
        {
            Code = "MINI",
            NumCargoDestinations = 7,
            MandatoryFlights = 8,
            OptionalFlightsSetII = 6,
            NumOds = 40,
            RevenueTkmTarget = 800_000,
            Fleets =
            [
                AirlineProfile.RC.Fleets[0] with { Count = 3 },
            ],
        };
        var inst = new InstanceGenerator(profile, seed).Build(2);
        AssertMatchesDirectMip(inst, withMaintenance: false);
    }

    [Fact]
    public void Reports_gap_and_progress()
    {
        var inst = TestInstances.Small();
        var bpc = new BranchAndPrice(inst, new BpcOptions
        {
            WithMaintenance = false,
            GapTarget = 0.005,
            TimeLimitSeconds = 120,
        });
        int events = 0;
        bpc.Progress += _ => events++;
        var res = bpc.Solve();
        Assert.NotNull(res.Best);
        Assert.True(res.Gap <= 0.005 + 1e-9 || res.StopReason == "tree exhausted",
            $"gap {res.Gap}, stop: {res.StopReason}");
        Assert.True(events > 0);
        Assert.True(res.Exact);
        Assert.False(double.IsNaN(res.FirstIncumbentObjective));
    }

    [Fact]
    public void Maintenance_solution_respects_maintenance_constraints()
    {
        var inst = TestInstances.Small();
        var res = RunBpc(inst, withMaintenance: true, exactStrings: true);
        Assert.NotNull(res.Best);
        Assert.True(res.Best!.WithMaintenance);
        foreach (var s in res.Best.SelectedStrings)
            Assert.True(s.IsFeasible(inst, withMaintenance: true, out var why), why);
        // rotations connect through maintenance-length ground stops
        Assert.NotEmpty(res.Best.Rotations);
    }
}
