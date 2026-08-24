using Acsp.Core;
using Acsp.Data;
using Acsp.Solver;

namespace Acsp.Tests;

/// <summary>Tests for the large-instance scaling features: tiny-far od consolidation and
/// geographic zone rotation of the proposal targeting.</summary>
public class DesignScalingTests
{
    [Fact]
    public void Consolidation_conserves_tonnage_and_shrinks_the_od_set()
    {
        var inst = InstanceGenerator.Generate("RLA", 1, 1);
        var rep = OdConsolidator.Consolidate(inst, maxTonnes: 1.0, minKm: 4000);
        var coarse = rep.Coarse;

        Assert.True(coarse.Ods.Length < inst.Ods.Length, "consolidation must shrink the od set");
        Assert.Equal(inst.Ods.Length, coarse.Ods.Length + rep.MembersConsolidated - rep.PseudoOds);
        // total tonnage and volume conserved exactly
        Assert.Equal(inst.Ods.Sum(o => o.Weight), coarse.Ods.Sum(o => o.Weight), 1);
        Assert.Equal(inst.Ods.Sum(o => o.Volume), coarse.Ods.Sum(o => o.Volume), 1);
        // aggregate revenue potential conserved (rate is tonnage-weighted; cent rounding)
        double rev0 = inst.Ods.Sum(o => o.Weight * o.Rate);
        double rev1 = coarse.Ods.Sum(o => o.Weight * o.Rate);
        Assert.True(Math.Abs(rev1 - rev0) / rev0 < 1e-6,
            $"revenue drift {rev1 - rev0:F2} on {rev0:F0}");
        // flights, legs and airports untouched: only demand is coarsened
        Assert.Same(inst.Flights, coarse.Flights);
        Assert.Same(inst.Legs, coarse.Legs);
        Assert.Same(inst.Airports, coarse.Airports);

        // every pseudo od runs hub-to-hub with at least a day of delivery allowance
        var kept = coarse.Ods.Take(coarse.Ods.Length - rep.PseudoOds);
        foreach (var od in coarse.Ods.Skip(coarse.Ods.Length - rep.PseudoOds))
        {
            Assert.True(coarse.Airports[od.Origin].IsTransferHub, "pseudo origin must be a hub");
            Assert.True(coarse.Airports[od.Destination].IsTransferHub, "pseudo dest must be a hub");
            Assert.True(od.MaxDeliveryTime >= 1440, "pseudo od needs at least a day");
        }
        // untouched ods survive identical (first kept od equals some original od exactly)
        foreach (var od in kept.Take(50))
            Assert.Contains(inst.Ods, o => o.Origin == od.Origin && o.Destination == od.Destination
                && o.Avail == od.Avail && Math.Abs(o.Weight - od.Weight) < 1e-9);
    }

    [Fact]
    public void Consolidation_is_a_noop_without_hubs_or_matches()
    {
        var inst = InstanceGenerator.Generate("RC", 1, 1);
        // RC is regional: nothing is farther than 4000 km from HKG, so nothing consolidates
        var rep = OdConsolidator.Consolidate(inst, maxTonnes: 1.0, minKm: 40000);
        Assert.Equal(0, rep.PseudoOds);
        Assert.Equal(inst.Ods.Length, rep.Coarse.Ods.Length);
    }

    [Theory]
    [InlineData("RC")]
    [InlineData("MI")]
    [InlineData("RLA")]
    public void Cover_constructor_builds_a_feasible_schedule(string airline)
    {
        var inst = InstanceGenerator.Generate(airline, 1, 1);
        var res = CoverConstructor.Build(inst);
        Assert.True(res.Solution is not null,
            "cover failed: " + string.Join("; ", res.Uncovered.Take(5).Select(u => u.Reason)));
        SolutionAssembler.AssembleRotations(inst, res.Solution!);
        var feas = FeasibilityChecker.Check(inst, res.Solution!);
        Assert.True(feas.IsFeasible, feas.ToString());
        // every mandatory flight covered exactly once
        var covered = res.Solution!.SelectedStrings.SelectMany(s => s.FlightIds).ToHashSet();
        foreach (var f in inst.MandatoryFlights)
            Assert.Contains(f.Id, covered);
    }

    [Fact]
    public void Seed_flow_loader_monetizes_a_cover_schedule()
    {
        var inst = InstanceGenerator.Generate("RC", 1, 1);
        var cover = CoverConstructor.Build(inst);
        Assert.NotNull(cover.Solution);
        // MIP heuristic OFF: any positive incumbent must come from the seed flow loader
        var bpc = new BranchAndPrice(inst, new Acsp.Solver.BpcOptions
        {
            TimeLimitSeconds = 30, SeedSolution = cover.Solution,
            MipHeuristicFrequency = 0, LoadSeedFlows = true, LpBackend = "highs",
        });
        var res = bpc.Solve();
        Assert.NotNull(res.Best);
        Assert.True(res.Objective > 0,
            $"loader should turn the cover schedule profitable, got {res.Objective:F0}");
    }

    [Fact]
    public void Zone_filter_restricts_proposal_targets()
    {
        var inst = InstanceGenerator.Generate("MI", 1, 1);
        var shipped = new double[inst.Ods.Length]; // nothing shipped: everything is a target
        var hubs = inst.Airports.Where(a => a.IsTransferHub).ToList();
        Assert.True(hubs.Count >= 2);
        double Dist(Airport a, Airport b)
        {
            double dLat = (b.Lat - a.Lat) * Math.PI / 180, dLon = (b.Lon - a.Lon) * Math.PI / 180;
            double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(a.Lat * Math.PI / 180) * Math.Cos(b.Lat * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 6371.0 * 2 * Math.Asin(Math.Min(1, Math.Sqrt(h)));
        }
        int zoneHub = hubs[0].Id;
        bool InZone(int a) => hubs.OrderBy(h => Dist(inst.Airports[a], h)).First().Id == zoneHub;

        var res = FlightProposer.Propose(inst, shipped, maxProposals: 40,
            includeCapacityTargets: true, includeDirect: true, includeExternalFallback: true,
            includeTrunks: true, zoneFilter: InZone);
        Assert.True(res.Proposals.Count > 0);
        var codeOf = inst.Airports.ToDictionary(a => a.Code, a => a.Id);
        foreach (var p in res.Proposals)
        {
            if (p.Reason.Contains("interhub trunk")) continue; // trunks are global by design
            var parts = p.TargetPair.Split("->");
            Assert.True(InZone(codeOf[parts[0]]) || InZone(codeOf[parts[1]]),
                $"proposal {p.Code} targets {p.TargetPair} outside the active zone");
        }

        // and without the filter, targets from both zones appear (sanity that the filter bites)
        var resAll = FlightProposer.Propose(inst, shipped, maxProposals: 40,
            includeCapacityTargets: true, includeDirect: true, includeExternalFallback: true,
            includeTrunks: true);
        bool anyOutside = resAll.Proposals
            .Where(p => !p.Reason.Contains("interhub trunk"))
            .Any(p =>
            {
                var parts = p.TargetPair.Split("->");
                return !InZone(codeOf[parts[0]]) && !InZone(codeOf[parts[1]]);
            });
        Assert.True(anyOutside, "unfiltered proposer should also target the other zone");
    }
}
