using System.Diagnostics;
using Acsp.Core;
using Acsp.Solver.Lp;

namespace Acsp.Solver;

public sealed record BpcOptions
{
    public bool WithMaintenance { get; init; }
    /// <summary>Stop when (bound - incumbent)/|incumbent| falls below this (paper: 0.5%).</summary>
    public double GapTarget { get; init; } = 0.005;
    public double TimeLimitSeconds { get; init; } = 3600;
    public int MaxNodes { get; init; } = 100_000;
    public string? LpBackend { get; init; } // null = auto: CPLEX if installed, else HiGHS
    public ColGenOptions ColGen { get; init; } = new();
    /// <summary>Run a MIP over the current columns as a primal heuristic every N nodes (0 = off).</summary>
    public int MipHeuristicFrequency { get; init; } = 40;
    public double MipHeuristicTimeLimit { get; init; } = 20;
    /// <summary>Warm-start column pool (e.g. from the previous design round); columns must be
    /// valid for the instance being solved.</summary>
    public IReadOnlyCollection<CargoPath>? SeedPaths { get; init; }
    public IReadOnlyCollection<FlightString>? SeedStrings { get; init; }
    /// <summary>Return the final column pool in BpcResult (for cross-round warm starts).</summary>
    public bool CollectColumnPool { get; init; }
    /// <summary>Known feasible solution accepted as the initial incumbent (e.g. the previous
    /// design round's schedule) and used as MIP-heuristic warm start. Must be feasible for
    /// the instance being solved; its columns should be present in the seed pool.</summary>
    public Solution? SeedSolution { get; init; }
    /// <summary>After the root's column generation, monetize a flow-less seed: fix its string
    /// selection in the LP and optimize the cargo flows over the columns the colgen just
    /// generated (one warm LP solve, no extra pricing) — the incumbent starts with revenue
    /// before the MIP heuristic runs.</summary>
    public bool LoadSeedFlows { get; init; } = true;
    /// <summary>Extend the colgen deadline while its own convergence gap (best valid bound vs
    /// current LP) is above ColGenGapThreshold, up to ColGenHardFactor x the time limit.</summary>
    public bool ColGenGapExtend { get; init; }
    public double ColGenGapThreshold { get; init; } = 0.03;
    public double ColGenHardFactor { get; init; } = 3.0;
    /// <summary>Run the MIP heuristic inside a local-branching ball around the incumbent
    /// (at most LocalBranchK selection flips, escalating x2, x4 while flat) instead of over
    /// the unrestricted master — the tractable "best move of &lt;= k changes" question. Only
    /// applies when an incumbent exists; falls back to the unrestricted MIP otherwise.</summary>
    public bool LocalBranching { get; init; }
    public int LocalBranchK { get; init; } = 60;
}

public sealed record BpcProgress(int NodesExplored, int NodesOpen, double Incumbent, double Bound,
    double Gap, int Paths, int Strings, int Cuts, double ElapsedSeconds, string Phase);

public sealed record BpcResult(
    Solution? Best, double Objective, double Bound, double Gap,
    double FirstIncumbentObjective, double FirstIncumbentSeconds,
    int NodesExplored, double ElapsedSeconds, bool Exact, string StopReason,
    List<CargoPath>? PathPool = null, List<FlightString>? StringPool = null);

/// <summary>
/// The branch and price and cut procedure of §5 (Fig. 3): depth-first branch and bound
/// (1-branch first) with column generation at every node, alternating pricing and cutting
/// iterations, the problem-specific branching strategies of §7 and implied bound cuts of §8.
/// Without maintenance the string pricer is exact and the procedure is an exact algorithm;
/// with maintenance the label-limited pricer makes it the approximative variant used in §9.
/// </summary>
public sealed class BranchAndPrice
{
    private readonly Instance _inst;
    private readonly BpcOptions _opt;
    public event Action<BpcProgress>? Progress;

    public BranchAndPrice(Instance inst, BpcOptions opt)
    {
        _inst = inst;
        _opt = opt;
    }

    public BpcResult Solve(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var rmp = new Rmp(_inst, _opt.WithMaintenance, LpSolverFactory.Create(_opt.LpBackend));
        rmp.SeedTrivialStrings();
        // warm start: re-inject the column pool of a previous, structurally similar solve
        if (_opt.SeedPaths is not null)
            foreach (var p in _opt.SeedPaths) rmp.AddPath(p);
        if (_opt.SeedStrings is not null)
            foreach (var s in _opt.SeedStrings) rmp.AddString(s);
        var colgen = new ColumnGeneration(_inst, rmp, _opt.ColGen);

        Solution? best = null;
        double incumbent = double.NegativeInfinity;
        double firstIncObj = double.NaN, firstIncTime = double.NaN;
        double rootBound = double.PositiveInfinity;
        int nodesExplored = 0;
        string stopReason = "tree exhausted";

        colgen.IterationProgress += (iter, obj, added) =>
            Progress?.Invoke(new BpcProgress(nodesExplored, -1, incumbent, obj,
                double.NaN, rmp.Paths.Count, rmp.Strings.Count, rmp.CutCount,
                sw.Elapsed.TotalSeconds, $"colgen iter {iter} (+{added} cols)"));

        var stack = new Stack<BranchState>();
        stack.Push(BranchState.Root(_inst));

        void Report(string phase)
        {
            double bound = OpenBound();
            Progress?.Invoke(new BpcProgress(nodesExplored, stack.Count, incumbent, bound,
                Gap(incumbent, bound), rmp.Paths.Count, rmp.Strings.Count, rmp.CutCount,
                sw.Elapsed.TotalSeconds, phase));
        }
        double processingBound = double.PositiveInfinity; // bound of the node being processed
        double OpenBound()
        {
            double b = stack.Count == 0 ? processingBound : Math.Max(stack.Max(s => s.InheritedBound), processingBound);
            if (double.IsPositiveInfinity(b) && stack.Count == 0) b = incumbent;
            return Math.Min(b, rootBound);
        }
        static double Gap(double inc, double bound)
        {
            if (double.IsNegativeInfinity(inc)) return double.PositiveInfinity;
            if (double.IsInfinity(bound)) return double.PositiveInfinity;
            return Math.Max(0, bound - inc) / Math.Max(1e-9, Math.Abs(inc));
        }

        void TryAcceptIncumbent(Solution sol, double obj, string source)
        {
            // NaN passes every <= comparison: a poisoned objective would be accepted and
            // then poison the incumbent guard itself for the rest of the run
            if (double.IsNaN(obj) || obj <= incumbent + 1e-6) return;
            SolutionAssembler.AssembleRotations(_inst, sol);
            var report = FeasibilityChecker.Check(_inst, sol);
            if (!report.IsFeasible)
            {
                // an infeasible candidate is REJECTED, never accepted — but it must not kill
                // a long run either: MIP solvers occasionally emit numerically borderline
                // solutions; the incumbent guard stays, the process survives
                Report($"incumbent rejected (infeasible, {source}: " +
                    $"{report.Violations.Count} violations, first: {report.Violations[0]})");
                return;
            }
            best = sol;
            incumbent = obj;
            if (double.IsNaN(firstIncObj))
            { firstIncObj = obj; firstIncTime = sw.Elapsed.TotalSeconds; }
            Report($"incumbent ({source})");
        }

        // a seeded incumbent makes "no solution" impossible: at worst the caller gets the
        // seed back (validated by the same feasibility check as any other incumbent)
        if (_opt.SeedSolution is { } seed)
        {
            SolutionAssembler.AssembleRotations(_inst, seed);
            TryAcceptIncumbent(seed, seed.Profit(_inst), "seed");
        }

        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) { stopReason = "cancelled"; break; }
            if (sw.Elapsed.TotalSeconds > _opt.TimeLimitSeconds) { stopReason = "time limit"; break; }
            if (nodesExplored >= _opt.MaxNodes) { stopReason = "node limit"; break; }

            var node = stack.Pop();
            if (!double.IsNegativeInfinity(incumbent) &&
                node.InheritedBound - incumbent <= Math.Abs(incumbent) * _opt.GapTarget)
                continue; // cannot improve by more than the gap target

            rmp.ApplyBranchingState(node.Restrictions, node.ForcedFlights, node.ForcedExternals,
                node.FixedStrings);
            processingBound = node.InheritedBound;
            var result = colgen.SolveNode(node.Restrictions, ct,
                deadline: () => sw.Elapsed.TotalSeconds > _opt.TimeLimitSeconds,
                gapExtend: _opt.ColGenGapExtend,
                gapThreshold: _opt.ColGenGapThreshold,
                hardDeadline: () =>
                    sw.Elapsed.TotalSeconds > _opt.TimeLimitSeconds * _opt.ColGenHardFactor);
            nodesExplored++;
            var lp = result.Lp;
            // when column generation was cut off by the deadline, lp.Objective is not a valid
            // bound (improving columns may remain) — the Farley bound in DualBound is
            if (nodesExplored == 1 && lp.Status == LpStatus.Optimal) rootBound = result.DualBound;

            if (lp.Status == LpStatus.Infeasible) { Report("pruned (infeasible)"); continue; }
            if (lp.Status != LpStatus.Optimal) { Report($"node status {lp.Status}"); continue; }
            if (rmp.ArtificialUsage(lp) > 1e-6)
            { Report("pruned (artificials in basis: infeasible)"); continue; }
            double nodeBound = Math.Min(result.DualBound, node.InheritedBound);
            processingBound = nodeBound;
            if (!double.IsNegativeInfinity(incumbent) &&
                nodeBound - incumbent <= Math.Abs(incumbent) * _opt.GapTarget)
            { Report("pruned (bound)"); continue; }

            if (rmp.IsIntegral(lp))
            {
                TryAcceptIncumbent(rmp.ExtractSolution(lp), lp.Objective, "node");
                continue;
            }

            // seed monetization: at the root, if the incumbent is a flow-less seed (cover
            // constructor output), fix its selection and optimize cargo flows over the pool
            // the colgen just generated — one warm LP, revenue before the MIP heuristic
            if (nodesExplored == 1 && _opt.LoadSeedFlows
                && best is { Flows.Count: 0 } seedNoFlows)
            {
                var loaded = rmp.SolveLpWithSelectionFixed(seedNoFlows);
                if (loaded.Status == LpStatus.Optimal && rmp.ArtificialUsage(loaded) <= 1e-6)
                    TryAcceptIncumbent(rmp.ExtractSolution(loaded), loaded.Objective,
                        "seed+flows");
            }

            // primal heuristic: MIP over the current columns
            if (_opt.MipHeuristicFrequency > 0 &&
                (nodesExplored == 1 || nodesExplored % _opt.MipHeuristicFrequency == 0))
            {
                if (_opt.LocalBranching && best is not null)
                {
                    // escalating ball: k, 2k, 4k sharing the budget; a flat ball widens, an
                    // adoption stops (the next heuristic pass search around the new incumbent)
                    double slice = _opt.MipHeuristicTimeLimit / 3;
                    bool adopted = false;
                    for (int e = 0; e < 3 && !adopted; e++)
                    {
                        int k = _opt.LocalBranchK << e;
                        double before = incumbent;
                        var mip = rmp.SolveMipLocalBranch(slice, best!, k);
                        if (mip.Status is LpStatus.Optimal or LpStatus.TimeLimit &&
                            rmp.IsIntegral(mip))
                            TryAcceptIncumbent(rmp.ExtractSolution(mip), mip.Objective,
                                $"local-branch k={k}");
                        adopted = incumbent > before + 1e-6;
                        if (!adopted) Report($"local-branch k={k} flat");
                    }
                    // re-monetize an adoption at the root: the ball chose the flights against
                    // the pool's flows, but the full LP over the adopted selection may find
                    // better cargo routings for the flights just switched on (root state only:
                    // the flow re-solve restores string bounds to [0,1])
                    if (adopted && nodesExplored == 1)
                    {
                        var loaded = rmp.SolveLpWithSelectionFixed(best!);
                        if (loaded.Status == LpStatus.Optimal &&
                            rmp.ArtificialUsage(loaded) <= 1e-6)
                            TryAcceptIncumbent(rmp.ExtractSolution(loaded), loaded.Objective,
                                "adopt+flows");
                    }
                }
                else
                {
                    var mip = rmp.SolveMipOnCurrentColumns(_opt.MipHeuristicTimeLimit, 1e-4, best);
                    if (mip.Status is LpStatus.Optimal or LpStatus.TimeLimit && rmp.IsIntegral(mip))
                        TryAcceptIncumbent(rmp.ExtractSolution(mip), mip.Objective,
                            "mip-heuristic");
                }
            }

            var decision = Branching.Decide(_inst, rmp, lp, node);
            if (decision is null)
            {
                // numerically integral after all; accept
                TryAcceptIncumbent(rmp.ExtractSolution(lp), lp.Objective, "node");
                continue;
            }
            decision.OneBranch.InheritedBound = nodeBound;
            decision.ZeroBranch.InheritedBound = nodeBound;
            stack.Push(decision.ZeroBranch);
            stack.Push(decision.OneBranch); // depth-first, 1-branch first (§7)
            if (nodesExplored % 10 == 1) Report($"branched on {decision.Kind}");

            // check achievable gap
            double open = OpenBound();
            if (!double.IsNegativeInfinity(incumbent) &&
                Gap(incumbent, open) <= _opt.GapTarget)
            { stopReason = "gap target reached"; break; }
        }

        // With the tree exhausted every open bound is gone: the incumbent is optimal within the
        // gap target (nodes are only pruned when they cannot improve by more than the target).
        double finalBound = stack.Count == 0 ? incumbent : OpenBound();
        sw.Stop();
        bool exact = !_opt.WithMaintenance; // heuristic string pricing => approximative bounds
        return new BpcResult(best, incumbent, finalBound, Gap(incumbent, finalBound),
            firstIncObj, firstIncTime, nodesExplored, sw.Elapsed.TotalSeconds, exact, stopReason,
            _opt.CollectColumnPool ? rmp.Paths.Select(pc => pc.Path).ToList() : null,
            _opt.CollectColumnPool ? rmp.Strings.Select(sc => sc.Str).ToList() : null);
    }
}
