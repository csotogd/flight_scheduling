using System.Globalization;
using Acsp.Core;
using Acsp.Data;
using Acsp.Solver;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

return args switch
{
    ["generate", .. var rest] => Generate(rest),
    ["profile", .. var rest] => ProfileCmd(rest),
    ["solve", .. var rest] => SolveCmd(rest),
    ["design", .. var rest] => Design(rest),
    ["cover", .. var rest] => Cover(rest),
    ["template", .. var rest] => Template(rest),
    ["benchmark", .. var rest] => Benchmark(rest),
    ["regional-bench", .. var rest] => RegionalBench(rest),
    ["diag", .. var rest] => Diag(rest),
    _ => Usage(),
};

static Instance? LoadInstance(string path)
{
    if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        return InstanceJson.Load(path);
    var res = InstanceXlsx.Read(File.ReadAllBytes(path),
        Path.GetFileNameWithoutExtension(path));
    foreach (var mfe in res.Messages)
        Console.WriteLine($"  [{mfe.Severity}] {mfe.Sheet} row {mfe.Row}: {mfe.Text}");
    if (!res.Ok) Console.WriteLine("import rejected: fix the errors above and retry");
    return res.Instance;
}

static int Cover(string[] a)
{
    var inst = LoadInstance(a[0]);
    if (inst is null) return 2;
    var res = CoverConstructor.Build(inst);
    Console.WriteLine($"mandatory flights: {inst.MandatoryFlights.Count()}");
    for (int k = 0; k < inst.Fleets.Length; k++)
        Console.WriteLine($"  {inst.Fleets[k].Code}: {res.FlightsPerFleet[k]} flights, " +
            $"{res.HoursPerFleet[k]:F0}h used of {inst.Fleets[k].Count * 168}h " +
            $"({inst.Fleets[k].Count} a/c)");
    foreach (var u in res.Uncovered.Take(15))
        Console.WriteLine($"  UNCOVERED {u.Code}: {u.Reason}");
    if (res.Solution is null)
    {
        Console.WriteLine($"cover FAILED: {res.Uncovered.Count} mandatory flights uncovered");
        return 2;
    }
    SolutionAssembler.AssembleRotations(inst, res.Solution);
    var feas = FeasibilityChecker.Check(inst, res.Solution);
    var aircraft = res.Solution.Rotations
        .GroupBy(r => r.FleetId)
        .ToDictionary(g => g.Key, g => g.Sum(r => r.AircraftNeeded(inst)));
    Console.WriteLine("rotation aircraft per fleet: " + string.Join(", ",
        aircraft.Select(kv => $"{inst.Fleets[kv.Key].Code} {kv.Value}/{inst.Fleets[kv.Key].Count}")));
    Console.WriteLine($"feasibility: {(feas.IsFeasible ? "OK" : feas.ToString())}");
    Console.WriteLine($"seed profit: {res.Solution.Profit(inst):F0} " +
        $"(fixed {res.Solution.FixedStringCosts(inst):F0} + aircraft {res.Solution.AircraftCosts(inst):F0})");
    return feas.IsFeasible ? 0 : 1;
}

static int Template(string[] a)
{
    if (a.Length == 0) { Console.WriteLine("usage: acsp template OUT.xlsx [--from INSTANCE.json | --airline X --set N --seed N]"); return 1; }
    string from = Opt(a, "from", "");
    var inst = from.Length > 0
        ? InstanceJson.Load(from)
        : InstanceGenerator.Generate(Opt(a, "airline", "RC"), int.Parse(Opt(a, "set", "1")),
            int.Parse(Opt(a, "seed", "1")));
    File.WriteAllBytes(a[0], InstanceXlsx.Build(inst));
    Console.WriteLine($"{a[0]}: {inst.Flights.Length} flights, {inst.Ods.Length} ODs " +
        $"({inst.Name}) — edit and re-import with 'solve' or 'design'");
    return 0;
}

static int Design(string[] a)
{
    var inst = LoadInstance(a[0]);
    if (inst is null) return 2;
    var opt = new DesignOptions
    {
        BatchSize = int.Parse(Opt(a, "batch", "100")),
        MaxRounds = int.Parse(Opt(a, "rounds", "8")),
        StopThreshold = double.Parse(Opt(a, "stop", "0.003")),
        EvictAfterRounds = int.Parse(Opt(a, "evict", "2")),
        RoundTimeLimitSeconds = double.Parse(Opt(a, "round-time", "180")),
        GapTarget = double.Parse(Opt(a, "gap", "0.005")),
        WithMaintenance = Flag(a, "maintenance"),
        IncludeDirect = !Flag(a, "no-direct"),
        IncludeExternal = !Flag(a, "no-external"),
        IncludeTrunks = !Flag(a, "no-trunks"),
        AmnestyRounds = int.Parse(Opt(a, "amnesty", "4")),
        FinalTimeLimitSeconds = double.Parse(Opt(a, "final-time", "900")),
        StopAfterFlatRounds = int.Parse(Opt(a, "flat", "3")),
        ConsolidateTinyFar = Flag(a, "consolidate"),
        SeedWithCover = !Flag(a, "no-cover"),
        LoadSeedFlows = !Flag(a, "no-seed-load"),
        ColGenGapExtend = Flag(a, "gap-extend"),
        ColGenGapThreshold = double.Parse(Opt(a, "gap-extend-threshold", "0.03")),
        TinyMaxTonnes = double.Parse(Opt(a, "tiny-max", "1.0")),
        TinyFarMinKm = double.Parse(Opt(a, "tiny-km", "4000")),
        ZoneRotation = Flag(a, "zones"),
        LocalBranching = !Flag(a, "no-local-branch"),
        LocalBranchK = int.Parse(Opt(a, "lb-k", "60")),
        IncludeWaves = Flag(a, "waves"), // opt-in: measured no-effect on RLA (ALGORITHM.md)
        RegionalPolish = Flag(a, "regional"),
    };
    var designer = new NetworkDesigner(inst, opt);
    var lastReport = DateTime.MinValue;
    designer.Progress += p =>
    {
        if (p.Solver is { } s)
        {
            if ((DateTime.Now - lastReport).TotalSeconds < 2 && !s.Phase.StartsWith("incumbent"))
                return;
            lastReport = DateTime.Now;
            Console.WriteLine($"  r{p.Round} [{s.ElapsedSeconds,6:F1}s] nodes {s.NodesExplored,5} " +
                $"inc {s.Incumbent,14:F0} bound {s.Bound,14:F0} {s.Phase}");
        }
        else
            Console.WriteLine($"  == round {p.Round}: {p.Phase}");
    };
    Console.WriteLine($"autonomous design on {inst.Name} " +
        $"(batch {opt.BatchSize}, up to {opt.MaxRounds} rounds, " +
        $"lp backend: {Acsp.Solver.Lp.LpSolverFactory.DefaultBackendName})");
    var res = designer.Run();
    Console.WriteLine($"\nstop: {res.StopReason}");
    Console.WriteLine($"base profit {res.BaseProfit,14:F0}");
    foreach (var r in res.Rounds)
        Console.WriteLine($"  round {r.Round}: profit {r.Profit,14:F0} gap {r.Gap:P2} " +
            $"flights {r.FlightsInModel} +{r.Added} flown {r.Flown} evicted {r.Evicted} " +
            $"({r.Seconds:F0}s) {r.Note}");
    if (res.Best.Best is null)
    {
        Console.WriteLine("no solution found; nothing to save " +
            "(try a larger --round-time so the base solve can find its first schedule)");
        return 2;
    }
    var feas = FeasibilityChecker.Check(res.BestInstance, res.Best.Best);
    Console.WriteLine($"feasibility: {(feas.IsFeasible ? "OK" : feas.ToString())}");
    double delta = res.Best.Objective - res.BaseProfit;
    Console.WriteLine($"best: round {res.BestRound}, profit {res.Best.Objective:F0} " +
        $"({(delta >= 0 ? "+" : "")}{delta:F0}, " +
        $"{(res.BaseProfit != 0 ? delta / Math.Abs(res.BaseProfit) : 0):P2} vs base)");
    var accepted = res.Proposals.Where(p => p.Status == "accepted").ToList();
    Console.WriteLine($"proposals: {res.Proposals.Count} tried, {accepted.Count} accepted");
    foreach (var p in accepted)
        Console.WriteLine($"  {p.Code}: {string.Join("->", p.Route)} for {p.TargetPair} " +
            $"({p.TargetTonnes:F0}t) — {p.Reason}");
    string outDir = Opt(a, "out", "results");
    var solPath = Path.Combine(outDir, res.BestInstance.Name.Replace("+prop", "") +
        "+design.solution.json");
    SolutionJson.Save(res.BestInstance, res.Best, solPath, SolutionJson.DesignReport(res));
    Console.WriteLine($"best solution written to {solPath}");
    return 0;
}

static int Diag(string[] a)
{
    var inst = a[0].EndsWith(".json")
        ? InstanceJson.Load(a[0])
        : InstanceGenerator.Generate(a[0], int.Parse(a[1]), int.Parse(a[2]));
    bool mnt = Flag(a, "maintenance");
    using var rmp = new Rmp(inst, mnt, Acsp.Solver.Lp.LpSolverFactory.Create());
    rmp.SeedTrivialStrings();
    var colgen = new ColumnGeneration(inst, rmp, new ColGenOptions());
    var result = colgen.SolveNode(PricingRestrictions.AllowAll(inst));
    Console.WriteLine($"root LP: {result.Lp.Status}, obj {result.Lp.Objective:F0}, " +
        $"artificials {rmp.ArtificialUsage(result.Lp):F3}");
    // which artificials are active?
    var duals = rmp.GetDuals(result.Lp);
    var uncovered = new List<string>();
    foreach (var f in inst.CargoFlights)
    {
        double cover = rmp.Strings.Where(sc => sc.Str.FlightIds.Contains(f.Id))
            .Sum(sc => result.Lp.ColumnValues[sc.Col]);
        if (f.IsMandatory && cover < 0.999)
            uncovered.Add($"{f.Code} (cover {cover:F2}, fleets: " + string.Join('/',
                inst.Fleets.Where(k => inst.Compatible(k.Id, f.Id)).Select(k => k.Code)) + ")");
    }
    Console.WriteLine($"mandatory flights not fully covered: {uncovered.Count}");
    foreach (var u in uncovered.Take(15)) Console.WriteLine("  " + u);
    // fleet usage
    for (int k = 0; k < inst.Fleets.Length; k++)
    {
        double used = rmp.Strings.Where(sc => sc.Str.FleetId == k)
            .Sum(sc => result.Lp.ColumnValues[sc.Col] * sc.Chi);
        Console.WriteLine($"fleet {inst.Fleets[k].Code}: ~{used:F1} strings crossing count time, " +
            $"available {inst.Fleets[k].Count}");
    }

    // demand servability analysis: why is demand left on the table?
    var pricer = new PathPricer(inst);
    var allowAll = PricingRestrictions.AllowAll(inst);
    var probe = MasterDuals.Zero(inst);
    var zero = MasterDuals.Zero(inst);
    double tUnserv = 0, tUnprofitable = 0, tOk = 0;
    int nUnserv = 0, nUnprofitable = 0, nOk = 0;
    foreach (var od in inst.Ods)
    {
        probe.OdDemand[od.Id] = -1e9;
        bool exists = pricer.PriceOd(od, probe, allowAll) is not null;
        probe.OdDemand[od.Id] = 0;
        if (!exists) { nUnserv++; tUnserv += od.Weight; continue; }
        // profitable itinerary at zero duals (ignoring capacity competition)?
        bool profitable = pricer.PriceOd(od, zero, allowAll) is not null;
        if (!profitable) { nUnprofitable++; tUnprofitable += od.Weight; }
        else { nOk++; tOk += od.Weight; }
    }
    double total = inst.Ods.Sum(o => o.Weight);
    Console.WriteLine($"demand analysis over {inst.Ods.Length} ODs, {total:F0}t:");
    Console.WriteLine($"  no feasible itinerary at all:      {nUnserv,5} ODs {tUnserv,9:F0}t ({tUnserv / total:P1})");
    Console.WriteLine($"  itinerary exists but margin <= 0:  {nUnprofitable,5} ODs {tUnprofitable,9:F0}t ({tUnprofitable / total:P1})");
    Console.WriteLine($"  profitably servable (pre-capacity):{nOk,5} ODs {tOk,9:F0}t ({tOk / total:P1})");
    return 0;
}

static int Usage()
{
    Console.WriteLine("""
        ACSP - Air cargo scheduling (Derigs & Friederichs 2013)

        usage:
          acsp generate [--airline RC|IC|MI|EX|all] [--set 1|2|3|all] [--seeds N] [--out DIR]
          acsp solve INSTANCE.json|.xlsx [--maintenance] [--time-limit SEC] [--gap G] [--out DIR]
          acsp design INSTANCE.json|.xlsx [--batch 100] [--rounds 8] [--stop 0.003] [--evict 2]
                      [--round-time SEC] [--gap G] [--maintenance] [--out DIR]
          acsp template OUT.xlsx [--from INSTANCE.json | --airline X --set N --seed N]
          acsp benchmark [--airlines RC,IC,MI] [--sets 1,2,3] [--seeds N] [--maintenance]
                         [--time-limit SEC] [--gap G] [--out DIR]
        """);
    return 1;
}

static string Opt(string[] a, string name, string def)
{
    int i = Array.IndexOf(a, "--" + name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
}

static bool Flag(string[] a, string name) => Array.IndexOf(a, "--" + name) >= 0;

static int RegionalBench(string[] a)
{
    // A/B experiment for the geographic decomposition: one shared base incumbent, then the
    // SAME extra budget spent (A) continuing the global solve vs (B) cycling regional blocks.
    var inst = LoadInstance(a[0]);
    if (inst is null) return 2;
    double baseTime = double.Parse(Opt(a, "base-time", "900"));
    double armTime = double.Parse(Opt(a, "arm-time", "1800"));
    double blockTime = double.Parse(Opt(a, "block-time", "450"));

    Console.WriteLine($"== base solve ({baseTime:F0}s)");
    var cover = CoverConstructor.Build(inst);
    if (cover.Solution is not null) SolutionAssembler.AssembleRotations(inst, cover.Solution);
    var basePbc = new BranchAndPrice(inst, new BpcOptions
    {
        TimeLimitSeconds = baseTime, SeedSolution = cover.Solution, LoadSeedFlows = true,
        LocalBranching = true, MipHeuristicTimeLimit = Math.Max(20, baseTime * 0.3),
        CollectColumnPool = true,
    });
    basePbc.Progress += p => { if (p.Phase.StartsWith("incumbent")) Console.WriteLine(
        $"  base [{p.ElapsedSeconds,7:F1}s] inc {p.Incumbent,15:F0} {p.Phase}"); };
    var baseRes = basePbc.Solve();
    if (baseRes.Best is null) { Console.WriteLine("base solve found no incumbent"); return 2; }
    double p0 = baseRes.Objective;
    Console.WriteLine($"base incumbent: {p0:F0} (gap {baseRes.Gap:P1})");

    double pA = p0;
    if (!Flag(a, "skip-global"))
    {
        Console.WriteLine($"== arm A: global continuation ({armTime:F0}s)");
        var armA = new BranchAndPrice(inst, new BpcOptions
        {
            TimeLimitSeconds = armTime, SeedSolution = baseRes.Best, LoadSeedFlows = false,
            SeedPaths = baseRes.PathPool, SeedStrings = baseRes.StringPool,
            LocalBranching = true, MipHeuristicTimeLimit = Math.Max(20, armTime * 0.3),
        });
        armA.Progress += p => { if (p.Phase.StartsWith("incumbent")) Console.WriteLine(
            $"  A [{p.ElapsedSeconds,7:F1}s] inc {p.Incumbent,15:F0} {p.Phase}"); };
        var resA = armA.Solve();
        pA = Math.Max(p0, resA.Objective);
        Console.WriteLine($"arm A: {pA:F0} ({pA - p0:+0;-0;+0} vs base)");
    }

    Console.WriteLine($"== arm B: regional cycle (blocks {blockTime:F0}s, total {armTime:F0}s)");
    var reg = new RegionalOptimizer(inst, new RegionalOptions
    {
        BlockTimeLimitSeconds = blockTime, Cycles = 99, TotalTimeLimitSeconds = armTime,
    });
    reg.Progress += Console.WriteLine;
    var (bestB, pB, blocksLog) = reg.Run(baseRes.Best);
    Console.WriteLine($"arm B: {pB:F0} ({pB - p0:+0;-0;+0} vs base)");
    foreach (var b in blocksLog)
        Console.WriteLine($"  [{b.Region}] {b.Flights}f/{b.Ods}od " +
            $"{b.ProfitBefore:F0} -> {b.ProfitAfter:F0} ({b.Seconds:F0}s) {b.Note}");

    var feasB = FeasibilityChecker.Check(inst, bestB);
    Console.WriteLine($"arm B feasibility: {(feasB.IsFeasible ? "OK" : feasB.ToString())}");
    Console.WriteLine($"VERDICT: base {p0:F0} | global +{pA - p0:F0} | regional +{pB - p0:F0} " +
        $"-> {(pB > pA ? "REGIONAL wins" : pA > pB ? "GLOBAL wins" : "tie")}");
    Directory.CreateDirectory("results");
    File.AppendAllText("results/regional-bench.csv",
        $"{DateTime.Now:yyyy-MM-dd HH:mm},{inst.Name},{baseTime},{armTime},{blockTime}," +
        $"{p0:F0},{pA:F0},{pB:F0}\n");
    return 0;
}

static int ProfileCmd(string[] a)
{
    if (a.Length < 2)
    {
        Console.WriteLine("usage: acsp profile CODE OUT.json   (export a built-in airline " +
            "profile as an editable configuration file; use it with 'generate --airline OUT.json')");
        return 1;
    }
    var p = AirlineProfile.Get(a[0]);
    ProfileJson.Save(p, a[1]);
    Console.WriteLine($"{a[1]}: profile {p.Code} — {p.HubCodes.Length} hubs, " +
        $"{p.Fleets.Length} fleet types ({p.Fleets.Sum(f => f.Count)} aircraft), " +
        $"curfew {(p.CurfewStart < 0 ? "none" : $"{p.CurfewStart / 60:D2}:{p.CurfewStart % 60:D2}-{p.CurfewEnd / 60:D2}:{p.CurfewEnd % 60:D2}")}, " +
        $"handling {p.CargoHandlingMinutes}min. Edit and generate with " +
        $"'generate --airline {a[1]} --set 1 --seeds 1'");
    return 0;
}

static int Generate(string[] a)
{
    string airline = Opt(a, "airline", "all");
    string set = Opt(a, "set", "all");
    int seeds = int.Parse(Opt(a, "seeds", "5"));
    string dir = Opt(a, "out", "instances");
    var airlines = airline == "all" ? ["RC", "IC", "MI", "EX", "GI"] : new[] { airline };
    var sets = set == "all" ? [1, 2, 3] : new[] { int.Parse(set) };
    foreach (var al in airlines)
        foreach (var st in sets)
            for (int seed = 1; seed <= seeds; seed++)
            {
                // al is a built-in code (RC, RLA, ...) or the path of a profile JSON
                var inst = InstanceGenerator.Generate(ProfileJson.Resolve(al), st, seed);
                var path = Path.Combine(dir, inst.Name + ".json");
                InstanceJson.Save(inst, path);
                Console.WriteLine($"{path}: {inst.Flights.Length} flights " +
                    $"({inst.MandatoryFlights.Count()} mand, {inst.OptionalFlights.Count()} opt, " +
                    $"{inst.ExternalFlights.Count()} ext), {inst.Legs.Length} legs, {inst.Ods.Length} ODs");
            }
    return 0;
}

static int SolveCmd(string[] a)
{
    var inst = LoadInstance(a[0]);
    if (inst is null) return 2;
    bool mnt = Flag(a, "maintenance");
    double timeLimit = double.Parse(Opt(a, "time-limit", "1800"));
    double gap = double.Parse(Opt(a, "gap", "0.005"));
    var res = RunOne(inst, mnt, timeLimit, gap, verbose: true, noHeuristic: Flag(a, "no-heuristic"));
    string outDir = Opt(a, "out", "results");
    if (res.Best is not null)
    {
        var path = Path.Combine(outDir, inst.Name + (mnt ? "-mnt" : "") + ".solution.json");
        SolutionJson.Save(inst, res, path);
        Console.WriteLine($"solution written to {path}");
    }
    return res.Best is null ? 2 : 0;
}

static BpcResult RunOne(Instance inst, bool mnt, double timeLimit, double gap, bool verbose,
    bool noHeuristic = false)
{
    var bpc = new BranchAndPrice(inst, new BpcOptions
    {
        WithMaintenance = mnt,
        GapTarget = gap,
        TimeLimitSeconds = timeLimit,
        MipHeuristicFrequency = noHeuristic ? 0 : 40,
        // heuristic budget proportional to the solve budget (size-independent)
        MipHeuristicTimeLimit = Math.Max(20, timeLimit * 0.3),
    });
    var lastReport = DateTime.MinValue;
    bpc.Progress += p =>
    {
        if (!verbose) return;
        if ((DateTime.Now - lastReport).TotalSeconds < 2 && !p.Phase.StartsWith("incumbent")) return;
        lastReport = DateTime.Now;
        Console.WriteLine($"  [{p.ElapsedSeconds,7:F1}s] nodes {p.NodesExplored,5} " +
            $"inc {p.Incumbent,14:F0} bound {p.Bound,14:F0} gap {p.Gap,7:P2} " +
            $"cols {p.Paths + p.Strings,6} cuts {p.Cuts,4}  {p.Phase}");
    };
    if (verbose)
        Console.WriteLine($"solving {inst.Name} (maintenance: {mnt}, " +
            $"lp backend: {Acsp.Solver.Lp.LpSolverFactory.DefaultBackendName}) ...");
    var res = bpc.Solve();
    if (verbose)
    {
        Console.WriteLine($"  -> {res.StopReason}: obj {res.Objective:F0}, bound {res.Bound:F0}, " +
            $"gap {res.Gap:P2}, first incumbent {res.FirstIncumbentObjective:F0} " +
            $"@ {res.FirstIncumbentSeconds:F1}s, nodes {res.NodesExplored}, {res.ElapsedSeconds:F1}s");
        if (res.Best is not null)
        {
            var r = FeasibilityChecker.Check(inst, res.Best);
            Console.WriteLine($"  feasibility: {(r.IsFeasible ? "OK" : r.ToString())}");
            Console.WriteLine($"  profit {res.Best.Profit(inst):F0} = revenue {res.Best.Revenue(inst):F0} " +
                $"- variable {res.Best.VariableCosts(inst):F0} - fixed {res.Best.FixedStringCosts(inst):F0} " +
                $"- aircraft {res.Best.AircraftCosts(inst):F0}");
            Console.WriteLine($"  {res.Best.SelectedStrings.Count} strings, {res.Best.Rotations.Count} rotations, " +
                $"{res.Best.Flows.Count} flow paths, " +
                $"{res.Best.Flows.Sum(f => f.Tonnes):F1}t shipped");
        }
    }
    return res;
}

static int Benchmark(string[] a)
{
    var airlines = Opt(a, "airlines", "RC,IC,MI").Split(',');
    var sets = Opt(a, "sets", "1,2,3").Split(',').Select(int.Parse).ToArray();
    int seeds = int.Parse(Opt(a, "seeds", "1"));
    bool mnt = Flag(a, "maintenance");
    double timeLimit = double.Parse(Opt(a, "time-limit", "900"));
    double gap = double.Parse(Opt(a, "gap", "0.005"));
    string outDir = Opt(a, "out", "results");
    Directory.CreateDirectory(outDir);
    // append to an existing benchmark csv instead of overwriting it
    string csvPath = Path.Combine(outDir, $"benchmark{(mnt ? "-mnt" : "")}.csv");
    var csv = new List<string> { "set,seed,maintenance,firstIncTime,firstIncObj,bestObj,bound,gap,nodes,time,stop" };
    if (File.Exists(csvPath))
        csv.AddRange(File.ReadAllLines(csvPath).Skip(1).Where(l => l.Trim().Length > 0));
    foreach (var al in airlines)
        foreach (var st in sets)
            for (int seed = 1; seed <= seeds; seed++)
            {
                var inst = InstanceGenerator.Generate(al, st, seed);
                var res = RunOne(inst, mnt, timeLimit, gap, verbose: true);
                csv.Add(string.Join(',',
                    $"{al}-{st}", seed, mnt,
                    res.FirstIncumbentSeconds.ToString("F1"),
                    res.FirstIncumbentObjective.ToString("F0"),
                    res.Objective.ToString("F0"),
                    res.Bound.ToString("F0"),
                    res.Gap.ToString("F4"),
                    res.NodesExplored,
                    res.ElapsedSeconds.ToString("F1"),
                    res.StopReason));
                File.WriteAllLines(csvPath, csv);
                if (res.Best is not null)
                    SolutionJson.Save(inst, res, Path.Combine(outDir,
                        inst.Name + (mnt ? "-mnt" : "") + ".solution.json"));
            }
    Console.WriteLine($"\nresults in {outDir}/benchmark{(mnt ? "-mnt" : "")}.csv");
    return 0;
}
