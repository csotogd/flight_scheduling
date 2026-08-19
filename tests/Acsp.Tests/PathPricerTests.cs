using Acsp.Core;
using Acsp.Data;
using Acsp.Solver;

namespace Acsp.Tests;

public class PathPricerTests
{
    private static MasterDuals RandomDuals(Instance inst, int seed, bool withCuts)
    {
        var rng = new Random(seed);
        var d = MasterDuals.Zero(inst);
        for (int i = 0; i < inst.Ods.Length; i++) d.OdDemand[i] = rng.NextDouble() * 60;
        for (int i = 0; i < inst.Legs.Length; i++)
        {
            d.LegWeight[i] = rng.NextDouble() * 25;
            d.LegVolume[i] = rng.NextDouble() * 3;
        }
        for (int i = 0; i < inst.Flights.Length; i++) d.FlightCover[i] = (rng.NextDouble() - 0.5) * 800;
        for (int k = 0; k < inst.Fleets.Length; k++)
        {
            d.FleetSize[k] = rng.NextDouble() * 200;
            for (int f = 0; f < inst.Flights.Length; f++)
            {
                d.DepBalance[k, f] = (rng.NextDouble() - 0.5) * 300;
                d.ArrBalance[k, f] = (rng.NextDouble() - 0.5) * 300;
            }
        }
        if (withCuts)
            foreach (var f in inst.OptionalFlights)
                foreach (var od in inst.Ods)
                    if (rng.NextDouble() < 0.3)
                        d.ImpliedBoundCuts[(od.Id, f.Id)] = rng.NextDouble() * 40;
        return d;
    }

    private static void AssertMatchesBruteForce(Instance inst, MasterDuals duals, IEnumerable<Od> ods,
        int bfMaxLegs)
    {
        var pricer = new PathPricer(inst);
        var rest = PricingRestrictions.AllowAll(inst);
        foreach (var od in ods)
        {
            var best = pricer.PriceOd(od, duals, rest);
            double bfBest = BruteForce.AllFeasiblePaths(inst, od, bfMaxLegs)
                .Select(p => BruteForce.PathReducedCost(inst, p, duals))
                .DefaultIfEmpty(double.NegativeInfinity).Max();
            if (best is null)
            {
                Assert.True(bfBest <= 1e-6,
                    $"od {od.Id}: pricer found nothing but brute force has rc {bfBest}");
            }
            else
            {
                Assert.True(best.Path.IsFeasible(inst, out var why), $"od {od.Id}: {why}");
                double check = BruteForce.PathReducedCost(inst, best.Path, duals);
                Assert.Equal(check, best.ReducedCost, 6);
                // pricer must be at least as good as any brute-force path within bfMaxLegs
                Assert.True(best.ReducedCost >= bfBest - 1e-6,
                    $"od {od.Id}: pricer {best.ReducedCost} < brute force {bfBest}");
            }
        }
    }

    [Fact]
    public void Matches_brute_force_on_tiny_with_zero_duals() =>
        AssertMatchesBruteForce(TestInstances.Tiny(), MasterDuals.Zero(TestInstances.Tiny()),
            TestInstances.Tiny().Ods, bfMaxLegs: 5);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Matches_brute_force_on_small_with_random_duals(int seed)
    {
        var inst = TestInstances.Small();
        AssertMatchesBruteForce(inst, RandomDuals(inst, seed, withCuts: seed % 2 == 0),
            inst.Ods, bfMaxLegs: 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Matches_brute_force_on_generated_rc(int seed)
    {
        var inst = InstanceGenerator.Generate("RC", 1, 1);
        AssertMatchesBruteForce(inst, RandomDuals(inst, seed, withCuts: true),
            inst.Ods.Take(30), bfMaxLegs: 3);
    }

    [Fact]
    public void AStar_equals_dijkstra()
    {
        var inst = InstanceGenerator.Generate("RC", 1, 2);
        var duals = RandomDuals(inst, 5, withCuts: false);
        var pricer = new PathPricer(inst);
        var rest = PricingRestrictions.AllowAll(inst);
        foreach (var od in inst.Ods.Take(60))
        {
            var a = pricer.PriceOd(od, duals, rest);
            var d = pricer.PriceOd(od, duals, rest, useDijkstraOnly: true);
            Assert.Equal(a is null, d is null);
            if (a is not null && d is not null)
                Assert.Equal(a.ReducedCost, d.ReducedCost, 6);
        }
    }

    [Fact]
    public void Respects_leg_visibility_restrictions()
    {
        var inst = TestInstances.Tiny();
        var pricer = new PathPricer(inst);
        var rest = PricingRestrictions.AllowAll(inst);
        var duals = MasterDuals.Zero(inst);
        var od = inst.Ods[0]; // HUB->AAA, only served by leg 0 (flight F0)
        Assert.NotNull(pricer.PriceOd(od, duals, rest));
        rest.ExcludeFlight(inst, 0);
        Assert.Null(pricer.PriceOd(od, duals, rest));
    }
}
