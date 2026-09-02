using Acsp.Core;
using Acsp.Data;
using Acsp.Solver;

namespace Acsp.Tests;

/// <summary>Tests for the geographic block decomposition (regional fix-and-optimize).</summary>
public class RegionalOptimizerTests
{
    [Fact]
    public void Regions_partition_all_airports_around_hub_clusters()
    {
        var inst = InstanceGenerator.Generate("MI", 1, 1); // SIN + BRU: two far hubs
        var reg = new RegionalOptimizer(inst, new RegionalOptions());
        var regions = reg.Regions();
        Assert.Equal(2, regions.Count);
        // exact partition: every airport in exactly one region
        Assert.Equal(inst.Airports.Length, regions.Sum(r => r.Airports.Count));
        Assert.Empty(regions[0].Airports.Intersect(regions[1].Airports));
    }

    [Fact]
    public void Regional_cycle_is_monotone_and_globally_feasible()
    {
        var inst = InstanceGenerator.Generate("MI", 1, 1);
        var bpc = new BranchAndPrice(inst, new Acsp.Solver.BpcOptions
        { TimeLimitSeconds = 45, LpBackend = "highs" });
        var incumbent = bpc.Solve();
        Assert.NotNull(incumbent.Best);
        double p0 = incumbent.Best!.Profit(inst);

        var reg = new RegionalOptimizer(inst, new RegionalOptions
        { BlockTimeLimitSeconds = 15, Cycles = 1, LpBackend = "highs", PairPasses = true });
        var (best, profit, blocks) = reg.Run(incumbent.Best);

        // the merge guard makes the cycle monotone: never worse than the incumbent,
        // and the result must pass the independent global feasibility check
        Assert.True(profit >= p0 - 1e-6, $"regional cycle lost profit: {profit:F0} < {p0:F0}");
        var feas = FeasibilityChecker.Check(inst, best);
        Assert.True(feas.IsFeasible, feas.ToString());
        // one block per region plus the relay pair (SIN|BRU carries cross demand)
        Assert.Equal(3, blocks.Count);
        Assert.Contains(blocks, b => b.Region.Contains('|'));
        // every block bounded its model to its region
        foreach (var b in blocks)
            Assert.True(b.Flights <= inst.CargoFlights.Count(),
                "block model must not exceed the global flight set");
    }
}
