using Acsp.Core;

namespace Acsp.Solver;

public sealed record RegionalOptions
{
    /// <summary>Time budget per block solve in seconds.</summary>
    public double BlockTimeLimitSeconds { get; init; } = 450;
    /// <summary>After the single-region passes of each cycle, run PAIRWISE passes over the
    /// region unions with cross-region unserved/contracted demand (the relay: a cross od
    /// enters the pair block WHOLE, with exact windows — feeder, trunk and distribution are
    /// all re-decidable in one model, no cross-pass bookkeeping or estimates needed).</summary>
    public bool PairPasses { get; init; } = true;
    /// <summary>Full cycles over the regions (a total time budget can cut them short).</summary>
    public int Cycles { get; init; } = 2;
    /// <summary>Total wall-clock budget in seconds; 0 = unlimited (cycles decide).</summary>
    public double TotalTimeLimitSeconds { get; init; }
    /// <summary>Hubs closer than this are clustered into one region.</summary>
    public double ClusterKm { get; init; } = 3000;
    public double GapTarget { get; init; } = 0.005;
    public string? LpBackend { get; init; }
    public bool LocalBranching { get; init; } = true;
    public int LocalBranchK { get; init; } = 60;
}

public sealed record RegionalBlockResult(string Region, int Airports, int Flights, int Ods,
    double ProfitBefore, double ProfitAfter, double Seconds, string Note);

/// <summary>
/// Geographic block-coordinate descent for planet-scale instances: freeze everything outside
/// one region (including every intercontinental "backbone" flight) and re-optimize the
/// region's own flights, flows and fleet slice. Cross-boundary cargo is truncated at its
/// gateway hub with EXACT windows read from the frozen timetable — the regional segment must
/// end before its onward frozen connection departs (minus transfer), and inbound segments
/// only appear when their frozen feeder has landed — so any regional improvement splices
/// back into a feasible global schedule by construction, and the global profit can only go
/// up (a merge is accepted only when the independently verified global profit improves).
/// v1 scope: polish of an existing schedule. Cross-region demand that is currently
/// contracted end-to-end is not re-opened (that needs a relay mechanism across passes).
/// </summary>
public sealed class RegionalOptimizer
{
    private readonly Instance _inst;
    private readonly RegionalOptions _opt;
    public event Action<string>? Progress;

    public RegionalOptimizer(Instance inst, RegionalOptions opt)
    {
        _inst = inst;
        _opt = opt;
    }

    private double Dist(int a, int b) => HaversineKm(_inst.Airports[a], _inst.Airports[b]);

    /// <summary>Hub clusters (&lt; ClusterKm apart) and the airports assigned to each by
    /// nearest hub. Every airport lands in exactly one region.</summary>
    public List<(string Name, List<int> Hubs, HashSet<int> Airports)> Regions()
    {
        var hubs = _inst.Airports.Where(a => a.IsTransferHub).Select(a => a.Id).ToList();
        var clusters = new List<List<int>>();
        foreach (var h in hubs)
        {
            var c = clusters.FirstOrDefault(z => z.Any(x => Dist(x, h) < _opt.ClusterKm));
            if (c is null) clusters.Add([h]); else c.Add(h);
        }
        var regions = clusters.Select(c => (
            Name: string.Join('+', c.Select(h => _inst.Airports[h].Code)),
            Hubs: c,
            Airports: new HashSet<int>())).ToList();
        foreach (var a in _inst.Airports)
        {
            int nearest = hubs.OrderBy(h => Dist(a.Id, h)).First();
            regions.First(r => r.Hubs.Contains(nearest)).Airports.Add(a.Id);
        }
        return regions;
    }

    /// <summary>Runs the cycle: for each region, build the block, solve it seeded with the
    /// incumbent's regional part, merge back if the global profit improves. Returns the
    /// final (feasibility-checked) solution and the per-block log.</summary>
    public (Solution Best, double Profit, List<RegionalBlockResult> Blocks) Run(
        Solution incumbent, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var best = incumbent;
        SolutionAssembler.AssembleRotations(_inst, best);
        double bestProfit = best.Profit(_inst);
        var blocks = new List<RegionalBlockResult>();
        var regions = Regions();
        Progress?.Invoke($"regions: {string.Join(", ", regions.Select(r => r.Name))}");

        // pass plan per cycle: every single region, then — the relay — every region PAIR
        // that carries cross demand between its two sides, heaviest cross tonnage first
        List<(string Name, HashSet<int> Airports)> Passes(Solution cur)
        {
            var passes = regions
                .Select(r => (r.Name, Airports: r.Airports)).ToList();
            if (!_opt.PairPasses || regions.Count < 2) return passes;
            int RegionOf(int airport) =>
                regions.FindIndex(r => r.Airports.Contains(airport));
            // cross tonnage still on the table: unshipped + contracted, per region pair
            var shipped = new double[_inst.Ods.Length];
            foreach (var (p, t) in cur.Flows) shipped[p.OdId] += t;
            var cross = new Dictionary<(int, int), double>();
            foreach (var od in _inst.Ods)
            {
                double open = od.Weight - shipped[od.Id];
                if (open <= 1e-3) continue;
                int a = RegionOf(od.Origin), b = RegionOf(od.Destination);
                if (a == b) continue;
                var key = a < b ? (a, b) : (b, a);
                cross[key] = cross.GetValueOrDefault(key) + open;
            }
            foreach (var ((a, b), tonnes) in cross.OrderByDescending(kv => kv.Value))
            {
                if (tonnes < 1.0) continue;
                passes.Add(($"{regions[a].Name}|{regions[b].Name}",
                    [.. regions[a].Airports.Concat(regions[b].Airports)]));
            }
            return passes;
        }

        for (int cycle = 0; cycle < _opt.Cycles; cycle++)
            foreach (var (name, airports) in Passes(best))
            {
                if (ct.IsCancellationRequested) return (best, bestProfit, blocks);
                // hard budget: the next block only gets what remains of the total budget,
                // so the cycle cannot overshoot even when a block solve overruns its slice
                double remaining = _opt.TotalTimeLimitSeconds > 0
                    ? _opt.TotalTimeLimitSeconds - sw.Elapsed.TotalSeconds
                    : double.PositiveInfinity;
                if (remaining <= 0)
                {
                    Progress?.Invoke($"[{DateTime.Now:HH:mm:ss}] total budget exhausted " +
                        $"({sw.Elapsed.TotalSeconds:F0}s), stopping");
                    return (best, bestProfit, blocks);
                }
                var t0 = sw.Elapsed.TotalSeconds;
                var block = BuildBlock(best, airports);
                if (block is null)
                {
                    blocks.Add(new(name, airports.Count, 0, 0, bestProfit, bestProfit, 0,
                        "no regional flights, skipped"));
                    continue;
                }
                var (sub, seed, map) = block.Value;
                Progress?.Invoke($"[{DateTime.Now:HH:mm:ss}] [{name}] block: " +
                    $"{sub.Airports.Length} airports, {sub.CargoFlights.Count()} flights, " +
                    $"{sub.Ods.Length} ods, fleet " +
                    string.Join("/", sub.Fleets.Select(k => k.Count)));
                var bpc = new BranchAndPrice(sub, new BpcOptions
                {
                    TimeLimitSeconds = Math.Min(_opt.BlockTimeLimitSeconds, remaining),
                    GapTarget = _opt.GapTarget, LpBackend = _opt.LpBackend,
                    SeedSolution = seed, LoadSeedFlows = false,
                    LocalBranching = _opt.LocalBranching, LocalBranchK = _opt.LocalBranchK,
                    MipHeuristicTimeLimit = Math.Max(20, _opt.BlockTimeLimitSeconds * 0.3),
                });
                var res = bpc.Solve(ct);
                double secs = sw.Elapsed.TotalSeconds - t0;
                if (res.Best is null)
                {
                    blocks.Add(new(name, airports.Count, sub.CargoFlights.Count(),
                        sub.Ods.Length, bestProfit, bestProfit, secs, "block found nothing"));
                    continue;
                }
                var merged = MergeBlock(best, res.Best, map);
                SolutionAssembler.AssembleRotations(_inst, merged);
                var feas = FeasibilityChecker.Check(_inst, merged);
                double profit = feas.IsFeasible ? merged.Profit(_inst) : double.NegativeInfinity;
                bool accepted = feas.IsFeasible && profit > bestProfit + 1e-6;
                blocks.Add(new(name, airports.Count, sub.CargoFlights.Count(), sub.Ods.Length,
                    bestProfit, accepted ? profit : bestProfit, secs,
                    !feas.IsFeasible ? $"merge infeasible, kept incumbent " +
                        $"({feas.Violations.Count}v: {feas.Violations[0]})"
                    : accepted ? $"accepted +{profit - bestProfit:F0}"
                    : "no improvement"));
                Progress?.Invoke($"[{DateTime.Now:HH:mm:ss}] [{name}] {blocks[^1].Note} " +
                    $"({secs:F0}s, cycle clock {sw.Elapsed.TotalSeconds:F0}s)");
                if (accepted) { best = merged; bestProfit = profit; }
            }
        return (best, bestProfit, blocks);
    }

    private sealed record BlockMap(
        HashSet<int> KeptFlights,               // global flight ids re-decided by the block
        int[] SubToGlobalFlight, int[] SubToGlobalLeg,
        // sub od id -> the global flow it was cut from (od, full path, run bounds), or a
        // whole-od entry (RunStart < 0) for fully-in-region demand
        (int GlobalOd, CargoPath? Path, double Tonnes, int RunStart, int RunEnd)[] OdSource);

    /// <summary>Builds the regional sub-instance, its seed (the incumbent's regional part)
    /// and the id maps. Null when the region has no re-decidable flight.</summary>
    private (Instance Sub, Solution Seed, BlockMap Map)? BuildBlock(
        Solution incumbent, HashSet<int> region)
    {
        var p = _inst.Period;
        // a flight is re-decidable ("kept") when every leg stays inside the region; a
        // degenerate cut (a flow entering and leaving the region at the same airport over
        // kept legs) freezes the involved flights instead — fixpoint, monotone shrinking
        var kept = _inst.Flights
            .Where(f => f.LegIds.All(l => region.Contains(_inst.Legs[l].Origin)
                && region.Contains(_inst.Legs[l].Destination)))
            .Select(f => f.Id).ToHashSet();
        while (true) // kept shrinks strictly every pass: termination is structural
        {
            var degenerate = new HashSet<int>();
            // (a) balance repair: the kept SELECTED strings must be balanced per
            // (fleet, airport) or the seed cannot assemble into cycles. Round trips are
            // self-balanced; the offenders are one-way strings whose mirror fell outside
            // the set (e.g. a multi-stop trunk with an en-route stop in a third region).
            // Freeze one contributing string per unbalanced (fleet, airport) and iterate —
            // cascades follow the unbalanced chain and stop there, instead of freezing
            // whole mixed rotations wholesale.
            var net = new Dictionary<(int Fleet, int Airport), int>();
            var oneWay = new List<(FlightString S, int O, int D)>();
            foreach (var st in incumbent.SelectedStrings)
            {
                if (!st.FlightIds.All(kept.Contains)) continue;
                int o = _inst.FlightOrigin(_inst.Flights[st.FlightIds[0]]);
                int d = _inst.FlightDestination(_inst.Flights[st.FlightIds[^1]]);
                if (o == d) continue;
                oneWay.Add((st, o, d));
                net[(st.FleetId, o)] = net.GetValueOrDefault((st.FleetId, o)) + 1;
                net[(st.FleetId, d)] = net.GetValueOrDefault((st.FleetId, d)) - 1;
            }
            foreach (var ((fleet, airport), n) in net.Where(kv => kv.Value > 0))
            {
                var offender = oneWay.FirstOrDefault(x =>
                    x.S.FleetId == fleet && x.O == airport
                    && x.S.FlightIds.All(f => !degenerate.Contains(f)));
                if (offender.S is not null)
                    foreach (var f in offender.S.FlightIds) degenerate.Add(f);
            }
            // (b) freeze the flights of degenerate cuts (a loop entering and leaving the
            // region at the same airport) and of paths crossing the region MORE THAN ONCE:
            // the merge splices exactly one regional segment per donated path — donating
            // two runs of the same flow double-counts its tonnage
            foreach (var (path, _) in incumbent.Flows)
            {
                var runs = Runs(path, kept).ToList();
                bool freeze = runs.Count > 1 || runs.Any(r =>
                    _inst.Legs[path.LegIds[r.Start]].Origin
                        == _inst.Legs[path.LegIds[r.End]].Destination
                    && !(r.Start == 0 && r.End == path.LegIds.Length - 1));
                if (!freeze) continue;
                foreach (var (s, e) in runs)
                    for (int i = s; i <= e; i++)
                        degenerate.Add(_inst.Legs[path.LegIds[i]].FlightId);
            }
            if (degenerate.Count == 0) break;
            kept.ExceptWith(degenerate);
        }
        if (kept.Count == 0 || !kept.Any(f => !_inst.Flights[f].IsExternal)) return null;

        // fleet slice: assemble the FROZEN strings alone and charge their aircraft need;
        // the remainder is the region's budget. (Charging whole mixed rotations would
        // double-count the aircraft that the region's own seed strings still need.)
        // fleet slice: the block may use the idle airplanes plus whatever the kept strings
        // themselves consume today — budget = count - used(all) + used(kept side). The kept
        // side assembles by construction after balance repair; if it still fails, skip the
        // block rather than crash (the cycle simply moves on).
        var frozenNeed = new int[_inst.Fleets.Length];
        try
        {
            var totalUsed = new int[_inst.Fleets.Length];
            foreach (var r in incumbent.Rotations)
                totalUsed[r.FleetId] += r.AircraftNeeded(_inst);
            var keptSol = new Solution
            {
                SelectedStrings = incumbent.SelectedStrings
                    .Where(st => st.FlightIds.All(kept.Contains)).ToList(),
                Flows = [], SelectedExternalFlights = [],
                WithMaintenance = incumbent.WithMaintenance,
            };
            SolutionAssembler.AssembleRotations(_inst, keptSol);
            var keptNeed = new int[_inst.Fleets.Length];
            foreach (var r in keptSol.Rotations)
                keptNeed[r.FleetId] += r.AircraftNeeded(_inst);
            for (int k = 0; k < _inst.Fleets.Length; k++)
                frozenNeed[k] = totalUsed[k] - keptNeed[k];
        }
        catch (InvalidOperationException) { return null; }

        // sub airports/fleets (dense re-ids; maintenance disabled — blocks run without it)
        var subAirports = region.OrderBy(x => x).ToList();
        var apMap = subAirports.Select((g, s) => (g, s)).ToDictionary(x => x.g, x => x.s);
        var airports = subAirports.Select((g, s) =>
        {
            var a = _inst.Airports[g];
            return new Airport
            {
                Id = s, Code = a.Code, Name = a.Name, Lat = a.Lat, Lon = a.Lon,
                IsTransferHub = a.IsTransferHub, MinTransferTime = a.MinTransferTime,
                TransferCostPerTonne = a.TransferCostPerTonne,
                StorageCostPerTonneHour = a.StorageCostPerTonneHour,
                CurfewStart = a.CurfewStart, CurfewEnd = a.CurfewEnd,
            };
        }).ToArray();
        var fleets = _inst.Fleets.Select(k => new FleetType
        {
            Id = k.Id, Code = k.Code,
            Count = Math.Max(0, k.Count - frozenNeed[k.Id]),
            FixedCostPerAircraft = k.FixedCostPerAircraft, MaxWeight = k.MaxWeight,
            MaxVolume = k.MaxVolume, RangeKm = k.RangeKm, RangeMaxKm = k.RangeMaxKm,
            PayloadAtMaxRangeT = k.PayloadAtMaxRangeT, CruiseSpeedKmH = k.CruiseSpeedKmH,
            DefaultMinGroundTime = k.DefaultMinGroundTime,
        }).ToArray();

        // sub flights/legs
        var legs = new List<Leg>();
        var flights = new List<Flight>();
        var subToGlobalFlight = new List<int>();
        var subToGlobalLeg = new List<int>();
        var legMap = new Dictionary<int, int>();
        foreach (var f in _inst.Flights.Where(f => kept.Contains(f.Id)))
        {
            int fid = flights.Count;
            var legIds = new List<int>();
            foreach (var l in f.LegIds)
            {
                var leg = _inst.Legs[l];
                legs.Add(new Leg
                {
                    Id = legs.Count, FlightId = fid,
                    Origin = apMap[leg.Origin], Destination = apMap[leg.Destination],
                    Dep = leg.Dep, Arr = leg.Arr, DistanceKm = leg.DistanceKm,
                    VariableCostPerTonne = leg.VariableCostPerTonne,
                    MaxWeight = leg.MaxWeight, MaxVolume = leg.MaxVolume,
                });
                legMap[l] = legs.Count - 1;
                subToGlobalLeg.Add(l);
                legIds.Add(legs.Count - 1);
            }
            flights.Add(new Flight
            {
                Id = fid, Code = f.Code, LegIds = [.. legIds],
                IsExternal = f.IsExternal, IsMandatory = f.IsMandatory,
                FixedCostByFleet = f.FixedCostByFleet, ExternalFixedCost = f.ExternalFixedCost,
                ForbiddenFleets = f.ForbiddenFleets,
            });
            subToGlobalFlight.Add(f.Id);
        }

        // sub demand: (a) regional runs of incumbent flows, with EXACT windows from the
        // frozen timetable; (b) whole ods with both endpoints in the region (their flows,
        // if any, are all-regional runs already; the contracted remainder rides along)
        var ods = new List<Od>();
        var source = new List<(int, CargoPath?, double, int, int)>();
        var seedFlows = new List<(CargoPath, double)>();
        var wholeOd = new Dictionary<int, int>(); // global od -> sub od id
        var shippedWhole = new double[_inst.Ods.Length];

        // the sub-instance runs with handling 0 and the handling ENCODED in the windows
        // (gateway hand-offs must not pay load/unload again, only origin/destination ends)
        int h = _inst.CargoHandlingMinutes;
        foreach (var od in _inst.Ods)
            if (region.Contains(od.Origin) && region.Contains(od.Destination))
            {
                if (od.MaxDeliveryTime - 2 * h <= 0) continue;
                wholeOd[od.Id] = ods.Count;
                source.Add((od.Id, null, od.Weight, -1, -1));
                ods.Add(new Od
                {
                    Id = ods.Count, Origin = apMap[od.Origin], Destination = apMap[od.Destination],
                    Avail = p.Wrap(od.Avail + h), MaxDeliveryTime = od.MaxDeliveryTime - 2 * h,
                    Weight = od.Weight, Volume = od.Volume, Rate = od.Rate,
                });
            }

        foreach (var (path, tonnes) in incumbent.Flows)
        {
            var od = _inst.Ods[path.OdId];
            // elapsed minutes since od.Avail at each leg boundary (monotone, no wrap issues)
            var elapsedArr = new int[path.LegIds.Length];
            int t = p.Time(od.Avail, _inst.Legs[path.LegIds[0]].Dep)
                + _inst.Legs[path.LegIds[0]].BlockTime(p);
            elapsedArr[0] = t;
            for (int i = 1; i < path.LegIds.Length; i++)
            {
                var prev = _inst.Legs[path.LegIds[i - 1]];
                var leg = _inst.Legs[path.LegIds[i]];
                t += p.Time(prev.Arr, leg.Dep) + leg.BlockTime(p);
                elapsedArr[i] = t;
            }
            foreach (var (s, e) in Runs(path, kept))
            {
                bool wholePath = s == 0 && e == path.LegIds.Length - 1;
                if (wholeOd.TryGetValue(path.OdId, out int who))
                {
                    // an od modeled WHOLE never donates runs too — that would duplicate its
                    // demand in the block. Fully-regional flows seed the whole od; partial
                    // runs (the incumbent routed it outside the region) are simply dropped
                    // and the block re-decides the od from scratch
                    if (wholePath)
                    {
                        seedFlows.Add((new CargoPath
                        {
                            OdId = who,
                            LegIds = [.. Enumerable.Range(s, e - s + 1)
                                .Select(i => legMap[path.LegIds[i]])],
                        }, tonnes));
                        shippedWhole[path.OdId] += tonnes;
                    }
                    continue;
                }
                if (tonnes <= 1e-6) continue;
                int oAp = _inst.Legs[path.LegIds[s]].Origin;
                int dAp = _inst.Legs[path.LegIds[e]].Destination;
                // avail: od origin after loading, or the frozen feeder's arrival + transfer
                int availElapsed = s == 0 ? h
                    : elapsedArr[s - 1] + _inst.Airports[oAp].MinTransferTime;
                // deadline: od deadline minus unloading, or the frozen onward departure
                // minus the gateway transfer
                int deadlineElapsed = e == path.LegIds.Length - 1 ? od.MaxDeliveryTime - h
                    : elapsedArr[e + 1] - _inst.Legs[path.LegIds[e + 1]].BlockTime(p)
                      - _inst.Airports[dAp].MinTransferTime;
                int window = deadlineElapsed - availElapsed;
                if (window <= 0) continue; // degenerate: the flow's flights stay kept; if the
                                           // block drops them the merge guard rejects it
                source.Add((path.OdId, path, tonnes, s, e));
                var sub = new Od
                {
                    Id = ods.Count, Origin = apMap[oAp], Destination = apMap[dAp],
                    Avail = p.Wrap(od.Avail + availElapsed), MaxDeliveryTime = window,
                    Weight = Math.Round(tonnes, 6),
                    Volume = Math.Round(tonnes * od.VolumePerTonne, 6), Rate = od.Rate,
                };
                ods.Add(sub);
                seedFlows.Add((new CargoPath
                {
                    OdId = sub.Id,
                    LegIds = [.. Enumerable.Range(s, e - s + 1).Select(i => legMap[path.LegIds[i]])],
                }, tonnes));
            }
        }
        if (ods.Count == 0) return null;

        var subInst = new Instance
        {
            Name = $"{_inst.Name}#block", Period = p, DeliverAll = _inst.DeliverAll,
            CargoHandlingMinutes = 0, // handling is encoded in the sub od windows
            Airports = airports, Fleets = fleets,
            Legs = [.. legs], Flights = [.. flights], Ods = [.. ods],
        };
        subInst.Validate();

        // seed: the incumbent's regional strings + its regional flow segments; under
        // deliver-all the remainder of every sub od is contracted
        var flightMap = subToGlobalFlight.Select((g, s) => (g, s)).ToDictionary(x => x.g, x => x.s);
        var seedStrings = incumbent.SelectedStrings
            .Where(st => st.FlightIds.All(kept.Contains))
            .Select(st => new FlightString
            {
                FleetId = st.FleetId,
                FlightIds = [.. st.FlightIds.Select(f => flightMap[f])],
            }).ToList();
        var seedExt = incumbent.SelectedExternalFlights.Where(kept.Contains)
            .Select(f => flightMap[f]).ToHashSet();
        var shipped = new double[ods.Count];
        foreach (var (sp, tn) in seedFlows) shipped[sp.OdId] += tn;
        var seed = new Solution
        {
            SelectedStrings = seedStrings, Flows = seedFlows, SelectedExternalFlights = seedExt,
            WithMaintenance = false,
            Contracted = subInst.DeliverAll
                ? [.. ods.Where(o => o.Weight - shipped[o.Id] > 1e-9)
                    .Select(o => (o.Id, Math.Round(o.Weight - shipped[o.Id], 6)))]
                : [],
        };
        return (subInst, seed, new BlockMap(kept, [.. subToGlobalFlight], [.. subToGlobalLeg],
            [.. source]));
    }

    /// <summary>Maximal runs of consecutive path legs whose flights are all kept.</summary>
    private IEnumerable<(int Start, int End)> Runs(CargoPath path, HashSet<int> kept)
    {
        int s = -1;
        for (int i = 0; i < path.LegIds.Length; i++)
        {
            bool k = kept.Contains(_inst.Legs[path.LegIds[i]].FlightId);
            if (k && s < 0) s = i;
            if (!k && s >= 0) { yield return (s, i - 1); s = -1; }
        }
        if (s >= 0) yield return (s, path.LegIds.Length - 1);
    }

    /// <summary>Splices the block's solution back into the global one: frozen strings,
    /// external bookings and flow segments stay; the region's are replaced. A sub flow on a
    /// run-od becomes the original path with its regional segment swapped; sub-contracted
    /// run tonnage falls back to global end-to-end contracting (conservative and feasible).</summary>
    private Solution MergeBlock(Solution global, Solution sub, BlockMap map)
    {
        var kept = map.KeptFlights;
        var strings = global.SelectedStrings.Where(st => !st.FlightIds.All(kept.Contains))
            .Concat(sub.SelectedStrings.Select(st => new FlightString
            {
                FleetId = st.FleetId,
                FlightIds = [.. st.FlightIds.Select(f => map.SubToGlobalFlight[f])],
            })).ToList();
        var ext = global.SelectedExternalFlights.Where(f => !kept.Contains(f))
            .Concat(sub.SelectedExternalFlights.Select(f => map.SubToGlobalFlight[f]))
            .ToHashSet();

        // flows: drop every global flow that donated a run (they are re-decided), keep the rest
        var donated = map.OdSource.Where(x => x.Path is not null).Select(x => x.Path!)
            .ToHashSet();
        var wholeOds = map.OdSource.Where(x => x.RunStart < 0).Select(x => x.GlobalOd)
            .ToHashSet();
        var flows = global.Flows
            .Where(fl => !donated.Contains(fl.Path) && !wholeOds.Contains(fl.Path.OdId))
            .ToList();
        var contracted = global.Contracted.Where(c => !wholeOds.Contains(c.OdId))
            .Select(c => (c.OdId, c.Tonnes)).ToList();
        var shortfall = new Dictionary<int, double>(); // global od -> tonnes to re-contract
        foreach (var (gOd, path, tonnes, s, _) in map.OdSource)
            if (path is not null) shortfall[gOd] = shortfall.GetValueOrDefault(gOd) + tonnes;
            else if (_inst.DeliverAll) shortfall[gOd] = _inst.Ods[gOd].Weight;

        foreach (var (subPath, tonnes) in sub.Flows)
        {
            var (gOd, path, _, s, e) = map.OdSource[subPath.OdId];
            var segment = subPath.LegIds.Select(l => map.SubToGlobalLeg[l]);
            int[] full = path is null
                ? [.. segment]
                : [.. path.LegIds.Take(s), .. segment, .. path.LegIds.Skip(e + 1)];
            flows.Add((new CargoPath { OdId = gOd, LegIds = full }, tonnes));
            shortfall[gOd] = shortfall.GetValueOrDefault(gOd) - tonnes;
        }
        if (_inst.DeliverAll)
            foreach (var (gOd, missing) in shortfall)
                if (missing > 1e-9) contracted.Add((gOd, Math.Round(missing, 6)));

        return new Solution
        {
            SelectedStrings = strings, Flows = flows, SelectedExternalFlights = ext,
            Contracted = contracted, WithMaintenance = global.WithMaintenance,
        };
    }

    private static double HaversineKm(Airport a, Airport b)
    {
        double dLat = (b.Lat - a.Lat) * Math.PI / 180, dLon = (b.Lon - a.Lon) * Math.PI / 180;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(a.Lat * Math.PI / 180) * Math.Cos(b.Lat * Math.PI / 180) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 6371.0 * 2 * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}
