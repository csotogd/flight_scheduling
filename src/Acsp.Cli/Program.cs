using System.Globalization;
using Acsp.Core;
using Acsp.Data;
using Acsp.Solver;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

return args switch
{
    ["generate", .. var rest] => Generate(rest),
    ["solve", .. var rest] => SolveCmd(rest),
    ["benchmark", .. var rest] => Benchmark(rest),
    ["diag", .. var rest] => Diag(rest),
    _ => Usage(),
};

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
    return 0;
}

static int Usage()
{
    Console.WriteLine("""
        ACSP - Air cargo scheduling (Derigs & Friederichs 2013)

        usage:
          acsp generate [--airline RC|IC|MI|EX|all] [--set 1|2|3|all] [--seeds N] [--out DIR]
          acsp solve INSTANCE.json [--maintenance] [--time-limit SEC] [--gap G] [--out DIR]
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

static int Generate(string[] a)
{
    string airline = Opt(a, "airline", "all");
    string set = Opt(a, "set", "all");
    int seeds = int.Parse(Opt(a, "seeds", "5"));
    string dir = Opt(a, "out", "instances");
    var airlines = airline == "all" ? ["RC", "IC", "MI", "EX"] : new[] { airline.ToUpperInvariant() };
    var sets = set == "all" ? [1, 2, 3] : new[] { int.Parse(set) };
    foreach (var al in airlines)
        foreach (var st in sets)
            for (int seed = 1; seed <= seeds; seed++)
            {
                var inst = InstanceGenerator.Generate(al, st, seed);
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
    var inst = InstanceJson.Load(a[0]);
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
        Console.WriteLine($"solving {inst.Name} (maintenance: {mnt}) ...");
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
    var csv = new List<string>
    {
        "set,seed,maintenance,firstIncTime,firstIncObj,bestObj,bound,gap,nodes,time,stop",
    };
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
                File.WriteAllLines(Path.Combine(outDir,
                    $"benchmark{(mnt ? "-mnt" : "")}.csv"), csv);
                if (res.Best is not null)
                    SolutionJson.Save(inst, res, Path.Combine(outDir,
                        inst.Name + (mnt ? "-mnt" : "") + ".solution.json"));
            }
    Console.WriteLine($"\nresults in {outDir}/benchmark{(mnt ? "-mnt" : "")}.csv");
    return 0;
}
