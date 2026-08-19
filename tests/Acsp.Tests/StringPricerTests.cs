using Acsp.Core;
using Acsp.Data;
using Acsp.Solver;

namespace Acsp.Tests;

public class StringPricerTests
{
    private static MasterDuals RandomDuals(Instance inst, int seed)
    {
        var rng = new Random(seed);
        var d = MasterDuals.Zero(inst);
        // cover duals strongly negative so that covering flights is attractive and
        // several strings have positive reduced cost
        for (int f = 0; f < inst.Flights.Length; f++) d.FlightCover[f] = -1500 - rng.NextDouble() * 2500;
        for (int i = 0; i < inst.Legs.Length; i++)
        {
            d.LegWeight[i] = rng.NextDouble() * 10;
            d.LegVolume[i] = rng.NextDouble() * 1.5;
        }
        for (int k = 0; k < inst.Fleets.Length; k++)
        {
            d.FleetSize[k] = rng.NextDouble() * 300;
            for (int f = 0; f < inst.Flights.Length; f++)
            {
                d.DepBalance[k, f] = (rng.NextDouble() - 0.5) * 400;
                d.ArrBalance[k, f] = (rng.NextDouble() - 0.5) * 400;
            }
        }
        foreach (var f in inst.OptionalFlights)
            foreach (var od in inst.Ods)
                if (rng.NextDouble() < 0.2)
                    d.ImpliedBoundCuts[(od.Id, f.Id)] = rng.NextDouble() * 15;
        return d;
    }

    [Theory]
    [InlineData(10080, 0, 9000, 2000, 1)]   // crosses count time once
    [InlineData(10080, 0, 100, 500, 0)]     // ends before wrapping to the count time
    [InlineData(10080, 0, 0, 10080, 1)]     // full week starting at the count time
    [InlineData(10080, 0, 0, 10081, 2)]     // slightly over a week
    [InlineData(10080, 5000, 4000, 900, 0)] // ends at 4900 < 5000
    [InlineData(10080, 5000, 4000, 1100, 1)]
    [InlineData(10080, 5000, 4000, 1100 + 10080, 2)]
    public void Chi_counts_crossings(int n, int countTime, int dep, long span, int expected)
    {
        var inst = TestInstances.Tiny();
        Assert.Equal(10080, n); // Tiny uses the weekly period
        var pricer = new StringPricer(inst, withMaintenance: false, countTime);
        Assert.Equal(expected, pricer.Chi(dep, span));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Farp_t_matches_brute_force(int seed)
    {
        var inst = TestInstances.Small();
        var duals = RandomDuals(inst, seed);
        var pricer = new StringPricer(inst, withMaintenance: false, countTime: 0);
        var rest = PricingRestrictions.AllowAll(inst);
        var found = pricer.Price(duals, rest, maxColumns: 1000);

        var bf = BruteForce.AllFeasibleStrings(inst, withMaintenance: false, maxFlights: 1)
            .Select(s => (Str: s, Rc: pricer.ReducedCost(s, duals)))
            .Where(x => x.Rc > 1e-6)
            .OrderByDescending(x => x.Rc)
            .ToList();

        Assert.Equal(bf.Count, found.Count);
        if (bf.Count > 0) Assert.Equal(bf[0].Rc, found[0].ReducedCost, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Farp_ts_exact_mode_matches_brute_force(int seed)
    {
        var inst = TestInstances.Small();
        var duals = RandomDuals(inst, seed);
        var pricer = new StringPricer(inst, withMaintenance: true, countTime: 0) { ExactMode = true };
        var rest = PricingRestrictions.AllowAll(inst);
        var found = pricer.Price(duals, rest, maxColumns: 100000);

        double bfBest = BruteForce.AllFeasibleStrings(inst, withMaintenance: true, maxFlights: 5)
            .Select(s => pricer.ReducedCost(s, duals))
            .DefaultIfEmpty(double.NegativeInfinity).Max();

        if (found.Count == 0)
        {
            Assert.True(bfBest <= 1e-6, $"pricer empty but brute force best is {bfBest}");
        }
        else
        {
            Assert.Equal(bfBest, found[0].ReducedCost, 5);
            foreach (var c in found.Take(50))
            {
                Assert.True(c.Str.IsFeasible(inst, withMaintenance: true, out var why), why);
                Assert.Equal(pricer.ReducedCost(c.Str, duals), c.ReducedCost, 5);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Farp_ts_heuristic_mode_returns_feasible_improving_strings(int seed)
    {
        var inst = TestInstances.Small();
        var duals = RandomDuals(inst, seed);
        var exact = new StringPricer(inst, withMaintenance: true, countTime: 0) { ExactMode = true };
        var heur = new StringPricer(inst, withMaintenance: true, countTime: 0);
        var rest = PricingRestrictions.AllowAll(inst);
        var exactFound = exact.Price(duals, rest, maxColumns: 100000);
        var heurFound = heur.Price(duals, rest, maxColumns: 200);

        foreach (var c in heurFound)
        {
            Assert.True(c.Str.IsFeasible(inst, withMaintenance: true, out var why), why);
            Assert.True(c.ReducedCost > 1e-6);
            Assert.Equal(heur.ReducedCost(c.Str, duals), c.ReducedCost, 5);
        }
        if (exactFound.Count > 0)
            Assert.True(heurFound.Count > 0, "heuristic found nothing although improving strings exist");
    }

    [Fact]
    public void Respects_fleet_flight_and_followon_restrictions()
    {
        var inst = TestInstances.Small();
        var duals = RandomDuals(inst, 0);
        var pricer = new StringPricer(inst, withMaintenance: true, countTime: 0) { ExactMode = true };

        var rest = PricingRestrictions.AllowAll(inst);
        rest.RestrictFleet(flightId: 0, fleetId: 0, force: true); // F0 only by fleet BIG
        var found = pricer.Price(duals, rest, maxColumns: 100000);
        Assert.DoesNotContain(found, c => c.Str.FleetId != 0 && c.Str.FlightIds.Contains(0));

        var rest2 = PricingRestrictions.AllowAll(inst);
        rest2.ForcedFollowOns[0] = 1; // any string covering F0 must fly F1 right after
        var found2 = pricer.Price(duals, rest2, maxColumns: 100000);
        foreach (var c in found2)
        {
            int idx = Array.IndexOf(c.Str.FlightIds, 0);
            if (idx >= 0)
            {
                Assert.True(idx + 1 < c.Str.FlightIds.Length, "string may not end after F0");
                Assert.Equal(1, c.Str.FlightIds[idx + 1]);
            }
        }

        var rest3 = PricingRestrictions.AllowAll(inst);
        rest3.ForbiddenFollowOns.Add((0, 1)); // F1 may not directly follow F0
        var found3 = pricer.Price(duals, rest3, maxColumns: 100000);
        foreach (var c in found3)
        {
            int idx = Array.IndexOf(c.Str.FlightIds, 0);
            if (idx >= 0 && idx + 1 < c.Str.FlightIds.Length)
                Assert.NotEqual(1, c.Str.FlightIds[idx + 1]);
        }
    }

    [Fact]
    public void Scales_to_generated_instances()
    {
        var inst = InstanceGenerator.Generate("RC", 2, 1);
        var duals = RandomDuals(inst, 0);
        var pricer = new StringPricer(inst, withMaintenance: true, countTime: 0);
        var rest = PricingRestrictions.AllowAll(inst);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = pricer.Price(duals, rest, maxColumns: 200);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 30_000, $"pricing took {sw.ElapsedMilliseconds}ms");
        foreach (var c in found.Take(20))
            Assert.True(c.Str.IsFeasible(inst, withMaintenance: true, out var why), why);
    }
}
