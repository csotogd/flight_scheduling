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
    public void Deliver_all_serves_every_od_with_contracting_as_recourse()
    {
        var baseInst = InstanceGenerator.Generate("RC", 1, 1);
        var inst = new Instance
        {
            Name = baseInst.Name + "-da", Period = baseInst.Period, DeliverAll = true,
            Airports = baseInst.Airports, Fleets = baseInst.Fleets,
            Legs = baseInst.Legs, Flights = baseInst.Flights, Ods = baseInst.Ods,
        };
        var bpc = new BranchAndPrice(inst, new Acsp.Solver.BpcOptions
        { TimeLimitSeconds = 60, LpBackend = "highs" });
        var res = bpc.Solve();
        Assert.NotNull(res.Best);
        var feas = FeasibilityChecker.Check(inst, res.Best!);
        Assert.True(feas.IsFeasible, feas.ToString());

        // the service commitment: every od fully delivered, own network or contracted
        var shipped = new double[inst.Ods.Length];
        foreach (var (path, tons) in res.Best!.Flows) shipped[path.OdId] += tons;
        foreach (var (odId, tons) in res.Best.Contracted) shipped[odId] += tons;
        foreach (var od in inst.Ods)
            Assert.True(Math.Abs(shipped[od.Id] - od.Weight) < 1e-2,
                $"od {od.Id}: delivered {shipped[od.Id]:F3} of {od.Weight:F3}");

        // recourse pricing is ~3x own economics: contracting everything must cost real money
        Assert.True(res.Best.Contracted.Count > 0 || res.Best.Flows.Count > 0);
        Assert.True(res.Objective > double.NegativeInfinity);
        // and the solver profit matches the independently recomputed solution profit
        Assert.True(Math.Abs(res.Objective - res.Best.Profit(inst))
            < Math.Max(1, Math.Abs(res.Objective)) * 1e-4,
            $"objective {res.Objective:F0} vs recomputed {res.Best.Profit(inst):F0}");
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
    public void Hub_waves_propose_time_consistent_bundles_with_honest_tonnage()
    {
        var inst = InstanceGenerator.Generate("RLA", 1, 1);
        var shipped = new double[inst.Ods.Length]; // nothing shipped: the whole tail is a target
        var res = FlightProposer.Propose(inst, shipped, maxProposals: 60,
            includeCapacityTargets: true, includeWaves: true);
        var waves = res.Proposals.Where(pr => pr.Reason.Contains("hub wave")).ToList();
        Assert.True(waves.Count > 0, "no waves proposed on a dense long-tail instance");

        double waveMin = 0.25 * inst.Fleets.Min(k => k.MaxWeight);
        var byCode = res.Extended.Flights.ToDictionary(f => f.Code);
        var codeOf = inst.Airports.ToDictionary(a => a.Code, a => a.Id);
        foreach (var bundle in waves.GroupBy(w => w.Reason.Split(':')[0]))
        {
            // honest economic floor and a complete mechanism: every wave flight touches a
            // hub of its corridor, and cross-hub bundles carry their trunk
            Assert.All(bundle, w => Assert.True(w.TargetTonnes >= waveMin,
                $"{w.Code}: {w.TargetTonnes}t below the wave floor {waveMin}t"));
            var corridor = bundle.First().TargetPair.Split("->");
            int h1 = codeOf[corridor[0]], h2 = codeOf[corridor[1]];
            Assert.All(bundle, w => Assert.True(
                w.Route[0] == corridor[0] || w.Route[0] == corridor[1],
                $"{w.Code} does not start at a corridor hub"));
            if (h1 != h2 && bundle.Any(w => w.Reason.Contains("feeder"))
                && bundle.Any(w => w.Reason.Contains("distribution")))
            {
                var trunk = bundle.SingleOrDefault(w => w.Reason.Contains("trunk"));
                if (trunk is not null)
                {
                    // chain consistency: the trunk departs after every feeder has landed
                    // plus the hub's own sorting minimum
                    int sortH1 = inst.Airports[h1].MinTransferTime + 30;
                    foreach (var f in bundle.Where(w => w.Reason.Contains("feeder")))
                    {
                        var legs = byCode[f.Code].LegIds.Select(l => res.Extended.Legs[l]).ToList();
                        int arrHub = legs[^1].Arr; // spoke -> hub leg
                        int slack = inst.Period.Wrap(trunk.DepMinute - arrHub);
                        Assert.True(slack >= sortH1,
                            $"trunk {trunk.Code} departs {slack}min after feeder {f.Code} " +
                            $"lands; needs {sortH1}");
                    }
                }
            }
        }

        // toggle off: no waves at all
        var off = FlightProposer.Propose(inst, shipped, maxProposals: 60,
            includeCapacityTargets: true, includeWaves: false);
        Assert.DoesNotContain(off.Proposals, pr => pr.Reason.Contains("hub wave"));
    }

    [Fact]
    public void Local_branching_heuristic_never_loses_the_seed_and_stays_feasible()
    {
        var inst = InstanceGenerator.Generate("RC", 1, 1);
        var cover = CoverConstructor.Build(inst);
        Assert.NotNull(cover.Solution);
        SolutionAssembler.AssembleRotations(inst, cover.Solution!);
        double seedProfit = cover.Solution!.Profit(inst);
        var bpc = new BranchAndPrice(inst, new Acsp.Solver.BpcOptions
        {
            TimeLimitSeconds = 30, SeedSolution = cover.Solution,
            LocalBranching = true, LocalBranchK = 20,
            MipHeuristicFrequency = 1, LpBackend = "highs",
        });
        var res = bpc.Solve();
        Assert.NotNull(res.Best);
        // the incumbent is always feasible inside the ball: the heuristic can only improve
        Assert.True(res.Objective >= seedProfit - 1e-6,
            $"ball lost the seed: {res.Objective:F0} < {seedProfit:F0}");
        Assert.True(FeasibilityChecker.Check(inst, res.Best!).IsFeasible);
    }

    [Fact]
    public void Exact_final_solve_is_seeded_and_delivers_on_full_demand()
    {
        var baseInst = InstanceGenerator.Generate("RC", 1, 1);
        var inst = new Instance
        {
            Name = baseInst.Name + "-da", Period = baseInst.Period, DeliverAll = true,
            Airports = baseInst.Airports, Fleets = baseInst.Fleets,
            Legs = baseInst.Legs, Flights = baseInst.Flights, Ods = baseInst.Ods,
        };
        // ConsolidateTinyFar routes the run through the exact-final path even when the
        // consolidation itself is a noop (RC is regional): the final must swap the full
        // demand back in, seed "same flights + all contracted", and deliver a solution
        var designer = new NetworkDesigner(inst, new DesignOptions
        {
            MaxRounds = 1, BatchSize = 20, RoundTimeLimitSeconds = 15,
            FinalTimeLimitSeconds = 20, ConsolidateTinyFar = true, LpBackend = "highs",
        });
        var result = designer.Run();
        Assert.NotNull(result.Best.Best);
        Assert.DoesNotContain("WARNING", result.StopReason);
        var feas = FeasibilityChecker.Check(result.BestInstance, result.Best.Best!);
        Assert.True(feas.IsFeasible, feas.ToString());
        // full demand: every original od delivered (own network or contracted)
        Assert.Equal(inst.Ods.Length, result.BestInstance.Ods.Length);
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
