using Acsp.Core;

namespace Acsp.Solver;

public sealed record DesignOptions
{
    /// <summary>Candidate flights proposed per round.</summary>
    public int BatchSize { get; init; } = 100;
    public int MaxRounds { get; init; } = 8;
    /// <summary>A round is "flat" when it improves the best profit by less than this fraction.</summary>
    public double StopThreshold { get; init; } = 0.003;
    /// <summary>Stop only after this many consecutive flat rounds: with rotating batches and
    /// amnesty, a single flat round does not mean the candidate space is exhausted.</summary>
    public int StopAfterFlatRounds { get; init; } = 3;
    /// <summary>Evict a proposal after this many consecutive rounds without being flown.</summary>
    public int EvictAfterRounds { get; init; } = 2;
    /// <summary>Time limit per branch-and-price run (the base solve gets 2x).</summary>
    public double RoundTimeLimitSeconds { get; init; } = 180;
    /// <summary>An evicted candidate may be proposed again this many rounds after eviction
    /// (the network context changes as accepted flights accumulate). 0 = never.</summary>
    public int AmnestyRounds { get; init; } = 4;
    /// <summary>Budget of the final rescue solve: base network + every candidate that was
    /// flown in at least one round (including later-evicted ones), solved once with a longer
    /// clock so synergies split across batches get a joint chance. 0 = skip.</summary>
    public double FinalTimeLimitSeconds { get; init; } = 900;
    /// <summary>Also propose direct no-hub rotations for pairs filling half an airplane.</summary>
    public bool IncludeDirect { get; init; } = true;
    /// <summary>Offer premium-cost external capacity for demand no own proposal reaches.</summary>
    public bool IncludeExternal { get; init; } = true;
    /// <summary>Propose inter-hub trunk shuttles aimed at cross-hub unserved tonnage.</summary>
    public bool IncludeTrunks { get; init; } = true;
    /// <summary>Coarsen tiny, far O&amp;Ds into hub-corridor pseudo demand for the design
    /// rounds (OdConsolidator); the final solve always runs on the full demand, so delivered
    /// schedules and profits stay exact. Off by default.</summary>
    public bool ConsolidateTinyFar { get; init; }
    public double TinyMaxTonnes { get; init; } = 1.0;
    public double TinyFarMinKm { get; init; } = 4000;
    /// <summary>Rotate proposal targeting across geographic zones (hub clusters), one zone
    /// per round plus one global round per cycle, so each zone gets a full batch and local
    /// synergies land in the same round. Off by default.</summary>
    public bool ZoneRotation { get; init; }
    /// <summary>Seed the base solve with the greedy cover constructor's schedule (guaranteed
    /// initial incumbent when the constructor succeeds; the MIP search then only improves).
    /// When the constructor fails, the base falls back to plain search plus escalation.</summary>
    public bool SeedWithCover { get; init; } = true;
    /// <summary>Monetize flow-less seeds at the root (fix selection, optimize flows over the
    /// generated pool with one warm LP).</summary>
    public bool LoadSeedFlows { get; init; } = true;
    /// <summary>Colgen deadline extension: keep converging past the soft deadline while the
    /// convergence gap exceeds ColGenGapThreshold, up to 3x the round budget.</summary>
    public bool ColGenGapExtend { get; init; }
    public double ColGenGapThreshold { get; init; } = 0.03;
    public double GapTarget { get; init; } = 0.005;
    public bool WithMaintenance { get; init; }
    public string? LpBackend { get; init; }
}

/// <summary>Lifecycle of one proposed flight across design rounds.</summary>
public sealed class TrackedProposal
{
    public required string Code { get; init; }
    public required string Key { get; init; }
    public required string[] Route { get; init; }
    public required int DepMinute { get; init; }
    public required string TargetPair { get; init; }
    public required double TargetTonnes { get; init; }
    public required string Reason { get; init; }
    public required int AddedRound { get; init; }
    public int LastFlownRound { get; set; } = -1;
    public string Status { get; set; } = "testing"; // testing | accepted | evicted
    public int EvictedRound { get; set; } = -1;
}

public sealed record DesignRound(int Round, double Profit, double Bound, double Gap,
    int FlightsInModel, int Added, int Flown, int Evicted, double Seconds, string Note);

public sealed record DesignProgress(int Round, string Phase, BpcProgress? Solver);

public sealed record DesignResult(Instance BestInstance, BpcResult Best, int BestRound,
    double BaseProfit, List<DesignRound> Rounds, List<TrackedProposal> Proposals,
    string StopReason);

/// <summary>
/// Autonomous network design: solve the base schedule, then iterate propose -> re-optimize ->
/// evict. Each round extends the instance with a batch of candidate OPTIONAL flights aimed at
/// demand the previous solution left on the ground (unroutable or crowded out); the optimizer
/// decides which candidates pay off, and candidates that stay unflown are evicted so the
/// master problem stays small. The pragmatic paradigm (§2.1) in a loop: the tool proposes,
/// the model decides, the planner reviews the final network.
/// </summary>
public sealed class NetworkDesigner
{
    private readonly Instance _base;
    private readonly DesignOptions _opt;
    public event Action<DesignProgress>? Progress;

    public NetworkDesigner(Instance baseInstance, DesignOptions opt)
    {
        _base = baseInstance;
        _opt = opt;
    }

    public DesignResult Run(CancellationToken ct = default)
    {
        var rounds = new List<DesignRound>();
        var tracked = new List<TrackedProposal>();
        var trackedByCode = new Dictionary<string, TrackedProposal>();
        // blueprint of every candidate that was flown in some round, kept for the final rescue
        var everFlown = new Dictionary<string, (Flight F, Leg[] Legs)>();

        // amnesty: a key is blocked while its proposal is alive or was evicted recently
        HashSet<string> ExcludedKeys(int round) => tracked
            .Where(t => t.Status != "evicted"
                || _opt.AmnestyRounds <= 0 || round - t.EvictedRound < _opt.AmnestyRounds)
            .Select(t => t.Key).ToHashSet();

        // optional coarsening: design rounds run on the consolidated demand; the final solve
        // below always runs on the full original demand (exact deliverable)
        var designBase = _base;
        if (_opt.ConsolidateTinyFar)
        {
            var rep = OdConsolidator.Consolidate(_base, _opt.TinyMaxTonnes, _opt.TinyFarMinKm);
            designBase = rep.Coarse;
            Progress?.Invoke(new DesignProgress(0,
                $"consolidated {rep.MembersConsolidated} tiny-far ods ({rep.TonnesConsolidated}t) " +
                $"into {rep.PseudoOds} hub-corridor pseudo ods " +
                $"({_base.Ods.Length} -> {designBase.Ods.Length})", null));
        }

        // geographic zones: cluster hubs within 3000 km into macro-zones; the cycle visits
        // each zone once plus one global round
        var zones = new List<List<int>>();
        if (_opt.ZoneRotation)
        {
            var hubIds = designBase.Airports.Where(a => a.IsTransferHub).Select(a => a.Id).ToList();
            double Dist(int a, int b) => HaversineKm(designBase.Airports[a], designBase.Airports[b]);
            foreach (var h in hubIds)
            {
                var zone = zones.FirstOrDefault(z => z.Any(x => Dist(x, h) < 3000));
                if (zone is null) zones.Add([h]); else zone.Add(h);
            }
            if (zones.Count < 2) zones.Clear(); // one zone = no rotation worth doing
        }
        int cycle = zones.Count + 1;

        // round 0: the base schedule. Greedy cover first: a constructive feasible schedule
        // (when one is found) becomes the guaranteed initial incumbent, and the MIP search
        // spends its whole budget improving instead of hunting for a first cover
        var current = designBase;
        if (_opt.SeedWithCover)
        {
            var cover = CoverConstructor.Build(current);
            if (cover.Solution is not null)
            {
                SolutionAssembler.AssembleRotations(current, cover.Solution);
                if (FeasibilityChecker.Check(current, cover.Solution).IsFeasible)
                {
                    _lastSchedule = cover.Solution;
                    Progress?.Invoke(new DesignProgress(0,
                        $"cover constructor seeded a feasible base schedule " +
                        $"({cover.Solution.SelectedStrings.Count} strings)", null));
                }
            }
            if (_lastSchedule is null)
                Progress?.Invoke(new DesignProgress(0,
                    "cover constructor found no feasible schedule; plain search", null));
        }
        var res = SolveOnce(current, 0, _opt.RoundTimeLimitSeconds * 2, ct);
        for (int attempt = 1; res.Best is null && attempt <= 3 && !ct.IsCancellationRequested;
            attempt++)
        {
            _poolPaths = null; _poolStrings = null; _lastSchedule = null; // fresh start
            double budget = _opt.RoundTimeLimitSeconds * 2 * Math.Pow(2, attempt);
            Progress?.Invoke(new DesignProgress(0,
                $"base retry {attempt}/3 (fresh, budget {budget:F0}s)", null));
            res = SolveOnce(current, 0, budget, ct);
        }
        if (res.Best is null)
            return new DesignResult(current, res, 0, double.NegativeInfinity, rounds, tracked,
                "base solve found no solution (budget escalation exhausted)");
        double baseProfit = res.Objective;
        var best = (Inst: current, Res: res, Round: 0);
        rounds.Add(new DesignRound(0, res.Objective, res.Bound, res.Gap,
            current.CargoFlights.Count(), 0, 0, 0, res.ElapsedSeconds, "base schedule"));
        Progress?.Invoke(new DesignProgress(0, "round-done", null));

        string stopReason = "max rounds reached";
        int consecutiveFailures = 0;
        int flatRounds = 0;
        int consecutiveEmpty = 0;
        // with zones, convergence needs a whole flat cycle: a flat Asia round says nothing
        // about whether Europe is exhausted
        int flatToStop = Math.Max(_opt.StopAfterFlatRounds, zones.Count > 0 ? cycle : 0);
        for (int r = 1; r <= _opt.MaxRounds; r++)
        {
            if (ct.IsCancellationRequested) { stopReason = "cancelled"; break; }

            // active zone for this round (zones.Count entries, then one global round)
            Func<int, bool>? zoneFilter = null;
            string zoneTag = "";
            if (zones.Count > 0)
            {
                int slot = (r - 1) % cycle;
                if (slot < zones.Count)
                {
                    var zoneHubs = zones[slot];
                    var inst0 = current;
                    double DistZ(int a, int b) => HaversineKm(inst0.Airports[a], inst0.Airports[b]);
                    var allHubs = inst0.Airports.Where(a => a.IsTransferHub).Select(a => a.Id).ToList();
                    zoneFilter = a => zoneHubs.Contains(allHubs.OrderBy(h => DistZ(a, h)).First());
                    zoneTag = $" [zone {string.Join('+', zoneHubs.Select(h => inst0.Airports[h].Code))}]";
                }
                else
                    zoneTag = " [global]";
            }

            var shipped = ShippedByOd(current, res);
            var prop = FlightProposer.Propose(current, shipped, _opt.BatchSize,
                codePrefix: $"P{r}-", excludeKeys: ExcludedKeys(r), includeCapacityTargets: true,
                includeDirect: _opt.IncludeDirect, includeExternalFallback: _opt.IncludeExternal,
                includeTrunks: _opt.IncludeTrunks, zoneFilter: zoneFilter);
            if (prop.Proposals.Count == 0)
            {
                // with zones an empty zone just passes its turn; stop only after a full
                // empty cycle (every zone plus the global round found nothing)
                if (zones.Count > 0 && ++consecutiveEmpty < cycle)
                {
                    rounds.Add(new DesignRound(r, res.Objective, res.Bound, res.Gap,
                        current.CargoFlights.Count(), 0, 0, 0, 0,
                        $"no candidates{zoneTag}, skipped"));
                    Progress?.Invoke(new DesignProgress(r, "round-done", null));
                    continue;
                }
                stopReason = "no more candidates";
                break;
            }
            consecutiveEmpty = 0;

            foreach (var p in prop.Proposals)
            {
                var t = new TrackedProposal
                {
                    Code = p.Code, Key = p.Key, Route = p.Route, DepMinute = p.DepMinute,
                    TargetPair = p.TargetPair, TargetTonnes = p.TargetTonnes,
                    Reason = p.Reason, AddedRound = r,
                };
                tracked.Add(t);
                trackedByCode[p.Code] = t;
            }
            Progress?.Invoke(new DesignProgress(r, "proposing", null));

            var candidate = prop.Extended;
            BpcResult candRes;
            try { candRes = SolveOnce(candidate, r, _opt.RoundTimeLimitSeconds, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // a failed round must not lose the work done so far: keep the best solution
                // found in earlier rounds and stop the loop gracefully
                stopReason = $"round {r} failed: {ex.Message.Split('\n')[0]}";
                foreach (var p in prop.Proposals)
                { trackedByCode[p.Code].Status = "evicted"; trackedByCode[p.Code].EvictedRound = r; }
                break;
            }
            if (candRes.Best is null)
            {
                // solver produced nothing within the budget (cannot happen once a schedule is
                // seeded): drop this batch and try a fresh one, but not forever
                foreach (var p in prop.Proposals)
                { trackedByCode[p.Code].Status = "evicted"; trackedByCode[p.Code].EvictedRound = r; }
                // the pool collected during the failed solve references the dropped batch's
                // flight/leg ids, which the NEXT batch will reuse: prune it to `current`'s ids
                _poolPaths = _poolPaths?.Where(p =>
                    p.LegIds.All(l => l < current.Legs.Length)).ToList();
                _poolStrings = _poolStrings?.Where(s =>
                    s.FlightIds.All(f => f < current.Flights.Length)).ToList();
                rounds.Add(new DesignRound(r, double.NegativeInfinity, candRes.Bound,
                    double.PositiveInfinity, candidate.CargoFlights.Count(),
                    prop.Proposals.Count, 0, prop.Proposals.Count, candRes.ElapsedSeconds,
                    "no solution, batch dropped"));
                Progress?.Invoke(new DesignProgress(r, "round-done", null));
                if (++consecutiveFailures >= 2)
                { stopReason = $"round {r} found no solution twice in a row"; break; }
                continue;
            }
            consecutiveFailures = 0;

            // which proposals were actually flown (or booked, for externals) this round?
            var flownCodes = new HashSet<string>(candRes.Best.SelectedStrings
                .SelectMany(s => s.FlightIds).Select(f => candidate.Flights[f].Code)
                .Concat(candRes.Best.SelectedExternalFlights
                    .Select(f => candidate.Flights[f].Code)));
            int flown = 0;
            foreach (var t in tracked.Where(t => t.Status != "evicted"))
                if (flownCodes.Contains(t.Code)) { t.LastFlownRound = r; flown++; }

            // remember the materialized flight of everything that flew, for the final rescue
            var byCode = candidate.Flights.ToDictionary(f => f.Code);
            foreach (var t in tracked)
                if (t.LastFlownRound == r && !everFlown.ContainsKey(t.Code)
                    && byCode.TryGetValue(t.Code, out var bf))
                    everFlown[t.Code] = (bf, bf.LegIds.Select(l => candidate.Legs[l]).ToArray());

            // evict proposals unflown for EvictAfterRounds consecutive rounds
            var evictCodes = tracked
                .Where(t => t.Status != "evicted"
                    && r - Math.Max(t.LastFlownRound, t.AddedRound - 1) >= _opt.EvictAfterRounds)
                .Select(t => t.Code).ToHashSet();
            foreach (var t in tracked.Where(t => evictCodes.Contains(t.Code)))
            { t.Status = "evicted"; t.EvictedRound = r; }

            if (evictCodes.Count == 0)
                current = candidate;
            else
            {
                (current, var flightMap, var legMap) = RemoveFlightsWithMap(candidate, evictCodes);
                RemapPool(flightMap, legMap);
            }
            res = candRes;

            double improvement = best.Res.Objective != 0
                ? (candRes.Objective - best.Res.Objective) / Math.Abs(best.Res.Objective)
                : double.PositiveInfinity;
            bool improved = candRes.Objective > best.Res.Objective;
            if (improved) best = (candidate, candRes, r);

            rounds.Add(new DesignRound(r, candRes.Objective, candRes.Bound, candRes.Gap,
                candidate.CargoFlights.Count(), prop.Proposals.Count, flown, evictCodes.Count,
                candRes.ElapsedSeconds,
                (improved ? $"profit +{improvement:P2}" : "no improvement") + zoneTag));
            Progress?.Invoke(new DesignProgress(r, "round-done", null));

            if (improvement < _opt.StopThreshold && r >= 2)
            {
                if (++flatRounds >= flatToStop)
                {
                    stopReason = $"converged ({flatRounds} consecutive rounds below " +
                        $"{_opt.StopThreshold:P2})";
                    break;
                }
            }
            else flatRounds = 0;
        }

        // final rescue solve: base network + everything that ever flew (including candidates
        // evicted later) with a longer clock, so synergies split across batches get one joint
        // chance; also serves as the deep polish of the delivered schedule
        if (_opt.FinalTimeLimitSeconds > 0 && !ct.IsCancellationRequested && res.Best is not null)
        {
            var inCurrent = current.Flights.Select(f => f.Code).ToHashSet();
            var rescued = tracked
                .Where(t => t.Status == "evicted" && t.LastFlownRound >= 0
                    && everFlown.ContainsKey(t.Code) && !inCurrent.Contains(t.Code))
                .Select(t => t.Code).Distinct().ToList();
            // when the rounds ran on consolidated demand, the final solve swaps the full
            // original demand back in — flight and leg ids are identical because the
            // consolidation only touches ods — so the delivered schedule, flows and profit
            // are exact; the coarse pool paths and schedule reference pseudo-od ids and are
            // dropped (the string pool stays valid and carries the warm start)
            var finalBase = current;
            bool exactFinal = false;
            if (_opt.ConsolidateTinyFar)
            {
                finalBase = new Instance
                {
                    Name = _base.Name, Period = _base.Period, DeliverAll = _base.DeliverAll,
                    Airports = _base.Airports,
                    Fleets = _base.Fleets, Legs = current.Legs, Flights = current.Flights,
                    Ods = _base.Ods,
                };
                finalBase.Validate();
                _poolPaths = null;
                _lastSchedule = null;
                exactFinal = true;
            }
            var finalInst = rescued.Count == 0 ? finalBase
                : AppendFlights(finalBase, rescued.Select(c => everFlown[c]));
            int rf = rounds[^1].Round + 1;
            Progress?.Invoke(new DesignProgress(rf,
                exactFinal ? "final exact solve (full demand)" : "final rescue solve", null));
            var finalRes = SolveOnce(finalInst, rf, _opt.FinalTimeLimitSeconds, ct);
            if (finalRes.Best is null && exactFinal && !ct.IsCancellationRequested)
            {
                // the exact final is the deliverable: one fresh escalation before giving up
                _poolPaths = null; _poolStrings = null; _lastSchedule = null;
                Progress?.Invoke(new DesignProgress(rf, "final retry (fresh, double budget)", null));
                finalRes = SolveOnce(finalInst, rf, _opt.FinalTimeLimitSeconds * 2, ct);
            }
            if (finalRes.Best is not null)
            {
                // an exact final replaces the coarse best unconditionally: objectives on
                // coarse vs full demand are not comparable
                bool improved = exactFinal || finalRes.Objective > best.Res.Objective;
                rounds.Add(new DesignRound(rf, finalRes.Objective, finalRes.Bound, finalRes.Gap,
                    finalInst.CargoFlights.Count(), rescued.Count,
                    0, 0, finalRes.ElapsedSeconds,
                    (exactFinal
                        ? $"final exact solve on full demand (+{rescued.Count} rescued)"
                        : $"final rescue solve (+{rescued.Count} rescued candidates)") +
                    (improved ? "" : ", no improvement")));
                if (improved) best = (finalInst, finalRes, rf);
                foreach (var t in tracked)   // rescued candidates are alive again for scoring
                    if (rescued.Contains(t.Code)) { t.Status = "testing"; t.EvictedRound = -1; }
                Progress?.Invoke(new DesignProgress(rf, "round-done", null));
            }
            else if (exactFinal)
                stopReason += "; WARNING: exact final solve found no solution, best is on " +
                    "consolidated demand";
        }

        // final statuses: flown (or booked) in the best solution = accepted
        var bestFlown = new HashSet<string>(best.Res.Best!.SelectedStrings
            .SelectMany(s => s.FlightIds).Select(f => best.Inst.Flights[f].Code)
            .Concat(best.Res.Best.SelectedExternalFlights
                .Select(f => best.Inst.Flights[f].Code)));
        foreach (var t in tracked.Where(t => t.Status != "evicted"))
            t.Status = bestFlown.Contains(t.Code) ? "accepted" : "evicted";

        return new DesignResult(best.Inst, best.Res, best.Round, baseProfit, rounds, tracked,
            stopReason);
    }

    // carried across rounds (warm start): the column pool and the last feasible schedule.
    // Round r's model differs from r-1's only by the new batch and the evicted flights, so
    // re-pricing everything from scratch is waste, and the previous schedule stays feasible
    // (new candidates simply unused) - seeding it guarantees every round returns a solution.
    private List<CargoPath>? _poolPaths;
    private List<FlightString>? _poolStrings;
    private Solution? _lastSchedule;

    private BpcResult SolveOnce(Instance inst, int round, double timeLimit, CancellationToken ct)
    {
        var bpc = new BranchAndPrice(inst, new BpcOptions
        {
            WithMaintenance = _opt.WithMaintenance,
            GapTarget = _opt.GapTarget,
            TimeLimitSeconds = timeLimit,
            LpBackend = _opt.LpBackend,
            // budget for the restricted-master MIP: a fraction of the solve budget rather
            // than any instance-size formula, so it scales with whatever the caller allows
            MipHeuristicTimeLimit = Math.Max(20, timeLimit * 0.3),
            SeedPaths = _poolPaths,
            SeedStrings = _poolStrings,
            CollectColumnPool = true,
            SeedSolution = _lastSchedule,
            LoadSeedFlows = _opt.LoadSeedFlows,
            ColGenGapExtend = _opt.ColGenGapExtend,
            ColGenGapThreshold = _opt.ColGenGapThreshold,
        });
        bpc.Progress += p => Progress?.Invoke(new DesignProgress(round, "solving", p));
        var res = bpc.Solve(ct);
        _poolPaths = res.PathPool ?? _poolPaths;
        _poolStrings = res.StringPool ?? _poolStrings;
        _lastSchedule = res.Best ?? _lastSchedule;
        return res;
    }

    /// <summary>Drops pool columns touching removed flights and remaps ids after an eviction.</summary>
    private void RemapPool(int[] flightMap, int[] legMap)
    {
        // the last schedule only contains flights that were flown this round, which are never
        // evicted in the same round - if a component nevertheless fails to map, drop the seed
        if (_lastSchedule is not null)
        {
            var strings = new List<FlightString>();
            var flows = new List<(CargoPath, double)>();
            var ext = new HashSet<int>();
            bool ok = true;
            foreach (var s in _lastSchedule.SelectedStrings)
            {
                var mapped = new int[s.FlightIds.Length];
                for (int i = 0; i < mapped.Length && ok; i++)
                    ok = (mapped[i] = flightMap[s.FlightIds[i]]) >= 0;
                if (ok) strings.Add(new FlightString { FleetId = s.FleetId, FlightIds = mapped });
            }
            foreach (var (p, t) in _lastSchedule.Flows)
            {
                var mapped = new int[p.LegIds.Length];
                for (int i = 0; i < mapped.Length && ok; i++)
                    ok = (mapped[i] = legMap[p.LegIds[i]]) >= 0;
                if (ok) flows.Add((new CargoPath { OdId = p.OdId, LegIds = mapped }, t));
            }
            foreach (var f in _lastSchedule.SelectedExternalFlights)
            {
                if (flightMap[f] < 0) { ok = false; break; }
                ext.Add(flightMap[f]);
            }
            _lastSchedule = ok
                ? new Solution
                {
                    SelectedStrings = strings, Flows = flows, SelectedExternalFlights = ext,
                    WithMaintenance = _lastSchedule.WithMaintenance,
                }
                : null;
        }
        if (_poolPaths is not null)
        {
            var paths = new List<CargoPath>(_poolPaths.Count);
            foreach (var p in _poolPaths)
            {
                var mapped = new int[p.LegIds.Length];
                bool ok = true;
                for (int i = 0; i < mapped.Length && ok; i++)
                    ok = (mapped[i] = legMap[p.LegIds[i]]) >= 0;
                if (ok) paths.Add(new CargoPath { OdId = p.OdId, LegIds = mapped });
            }
            _poolPaths = paths;
        }
        if (_poolStrings is not null)
        {
            var strings = new List<FlightString>(_poolStrings.Count);
            foreach (var s in _poolStrings)
            {
                var mapped = new int[s.FlightIds.Length];
                bool ok = true;
                for (int i = 0; i < mapped.Length && ok; i++)
                    ok = (mapped[i] = flightMap[s.FlightIds[i]]) >= 0;
                if (ok) strings.Add(new FlightString { FleetId = s.FleetId, FlightIds = mapped });
            }
            _poolStrings = strings;
        }
    }

    private static double HaversineKm(Airport a, Airport b)
    {
        double dLat = (b.Lat - a.Lat) * Math.PI / 180, dLon = (b.Lon - a.Lon) * Math.PI / 180;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(a.Lat * Math.PI / 180) * Math.Cos(b.Lat * Math.PI / 180) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 6371.0 * 2 * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static double[] ShippedByOd(Instance inst, BpcResult res)
    {
        var shipped = new double[inst.Ods.Length];
        if (res.Best is not null)
            foreach (var (path, tonnes) in res.Best.Flows)
                shipped[path.OdId] += tonnes;
        return shipped;
    }

    /// <summary>Appends flight blueprints (from another instance of the same geography) with
    /// freshly assigned dense ids; existing ids are untouched, so carried pools stay valid.</summary>
    public static Instance AppendFlights(Instance inst,
        IEnumerable<(Flight F, Leg[] Legs)> blueprints)
    {
        var legs = inst.Legs.ToList();
        var flights = inst.Flights.ToList();
        foreach (var (bf, blegs) in blueprints)
        {
            int id = flights.Count;
            var legIds = new List<int>();
            foreach (var l in blegs)
            {
                legs.Add(new Leg
                {
                    Id = legs.Count, FlightId = id, Origin = l.Origin,
                    Destination = l.Destination, Dep = l.Dep, Arr = l.Arr,
                    DistanceKm = l.DistanceKm, VariableCostPerTonne = l.VariableCostPerTonne,
                    TaxiMinutes = l.TaxiMinutes, MaxWeight = l.MaxWeight, MaxVolume = l.MaxVolume,
                });
                legIds.Add(legs.Count - 1);
            }
            flights.Add(new Flight
            {
                Id = id, Code = bf.Code, LegIds = [.. legIds], IsExternal = bf.IsExternal,
                IsMandatory = bf.IsMandatory, FixedCostByFleet = bf.FixedCostByFleet,
                ExternalFixedCost = bf.ExternalFixedCost, ForbiddenFleets = bf.ForbiddenFleets,
            });
        }
        var next = new Instance
        {
            Name = inst.Name, Period = inst.Period, DeliverAll = inst.DeliverAll,
            Airports = inst.Airports,
            Fleets = inst.Fleets, Legs = [.. legs], Flights = [.. flights], Ods = inst.Ods,
        };
        next.Validate();
        return next;
    }

    /// <summary>Rebuilds the instance without the given flights (ids stay dense).</summary>
    public static Instance RemoveFlights(Instance inst, ISet<string> codes) =>
        RemoveFlightsWithMap(inst, codes).Instance;

    /// <summary>RemoveFlights plus old-to-new id maps (-1 = removed) for pool remapping.</summary>
    public static (Instance Instance, int[] FlightMap, int[] LegMap) RemoveFlightsWithMap(
        Instance inst, ISet<string> codes)
    {
        var flightMap = new int[inst.Flights.Length];
        var legMap = new int[inst.Legs.Length];
        Array.Fill(flightMap, -1);
        Array.Fill(legMap, -1);
        var legs = new List<Leg>();
        var flights = new List<Flight>();
        foreach (var f in inst.Flights)
        {
            if (codes.Contains(f.Code)) continue;
            int id = flights.Count;
            flightMap[f.Id] = id;
            var legIds = new List<int>();
            foreach (var l in f.LegIds.Select(i => inst.Legs[i]))
            {
                legMap[l.Id] = legs.Count;
                legs.Add(new Leg
                {
                    Id = legs.Count, FlightId = id, Origin = l.Origin,
                    Destination = l.Destination, Dep = l.Dep, Arr = l.Arr,
                    DistanceKm = l.DistanceKm, VariableCostPerTonne = l.VariableCostPerTonne,
                    TaxiMinutes = l.TaxiMinutes, MaxWeight = l.MaxWeight, MaxVolume = l.MaxVolume,
                });
                legIds.Add(legs.Count - 1);
            }
            flights.Add(new Flight
            {
                Id = id, Code = f.Code, LegIds = [.. legIds], IsExternal = f.IsExternal,
                IsMandatory = f.IsMandatory, FixedCostByFleet = f.FixedCostByFleet,
                ExternalFixedCost = f.ExternalFixedCost, ForbiddenFleets = f.ForbiddenFleets,
            });
        }
        var next = new Instance
        {
            Name = inst.Name, Period = inst.Period, DeliverAll = inst.DeliverAll,
            Airports = inst.Airports,
            Fleets = inst.Fleets, Legs = [.. legs], Flights = [.. flights], Ods = inst.Ods,
        };
        next.Validate();
        return (next, flightMap, legMap);
    }
}
