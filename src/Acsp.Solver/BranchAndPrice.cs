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
    public string LpBackend { get; init; } = "highs";
    public ColGenOptions ColGen { get; init; } = new();
    /// <summary>Run a MIP over the current columns as a primal heuristic every N nodes (0 = off).</summary>
    public int MipHeuristicFrequency { get; init; } = 40;
    public double MipHeuristicTimeLimit { get; init; } = 20;
}

public sealed record BpcProgress(int NodesExplored, int NodesOpen, double Incumbent, double Bound,
    double Gap, int Paths, int Strings, int Cuts, double ElapsedSeconds, string Phase);

public sealed record BpcResult(
    Solution? Best, double Objective, double Bound, double Gap,
    double FirstIncumbentObjective, double FirstIncumbentSeconds,
    int NodesExplored, double ElapsedSeconds, bool Exact, string StopReason);

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
        var colgen = new ColumnGeneration(_inst, rmp, _opt.ColGen);

        Solution? best = null;
        double incumbent = double.NegativeInfinity;
        double firstIncObj = double.NaN, firstIncTime = double.NaN;
        double rootBound = double.PositiveInfinity;
        int nodesExplored = 0;
        string stopReason = "tree exhausted";

        var stack = new Stack<BranchState>();
        stack.Push(BranchState.Root(_inst));

        void Report(string phase)
        {
            double bound = OpenBound();
            Progress?.Invoke(new BpcProgress(nodesExplored, stack.Count, incumbent, bound,
                Gap(incumbent, bound), rmp.Paths.Count, rmp.Strings.Count, rmp.CutCount,
                sw.Elapsed.TotalSeconds, phase));
        }
        double OpenBound()
        {
            double b = stack.Count == 0 ? incumbent : stack.Max(s => s.InheritedBound);
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
            if (obj <= incumbent + 1e-6) return;
            SolutionAssembler.AssembleRotations(_inst, sol);
            var report = FeasibilityChecker.Check(_inst, sol);
            if (!report.IsFeasible)
                throw new InvalidOperationException(
                    $"solver produced an infeasible incumbent ({source}):\n{report}");
            best = sol;
            incumbent = obj;
            if (double.IsNaN(firstIncObj))
            { firstIncObj = obj; firstIncTime = sw.Elapsed.TotalSeconds; }
            Report($"incumbent ({source})");
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
            var result = colgen.SolveNode(node.Restrictions, ct);
            nodesExplored++;
            var lp = result.Lp;
            if (nodesExplored == 1) rootBound = lp.Status == LpStatus.Optimal ? lp.Objective : rootBound;

            if (lp.Status == LpStatus.Infeasible) { Report("pruned (infeasible)"); continue; }
            if (lp.Status != LpStatus.Optimal) { Report($"node status {lp.Status}"); continue; }
            double nodeBound = Math.Min(lp.Objective, node.InheritedBound);
            if (!double.IsNegativeInfinity(incumbent) &&
                nodeBound - incumbent <= Math.Abs(incumbent) * _opt.GapTarget)
            { Report("pruned (bound)"); continue; }

            if (rmp.IsIntegral(lp))
            {
                TryAcceptIncumbent(rmp.ExtractSolution(lp), lp.Objective, "node");
                continue;
            }

            // primal heuristic: MIP over the current columns
            if (_opt.MipHeuristicFrequency > 0 &&
                (nodesExplored == 1 || nodesExplored % _opt.MipHeuristicFrequency == 0))
            {
                var mip = rmp.SolveMipOnCurrentColumns(_opt.MipHeuristicTimeLimit, 1e-4);
                if (mip.Status is LpStatus.Optimal or LpStatus.TimeLimit && rmp.IsIntegral(mip))
                    TryAcceptIncumbent(rmp.ExtractSolution(mip), mip.Objective, "mip-heuristic");
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
            firstIncObj, firstIncTime, nodesExplored, sw.Elapsed.TotalSeconds, exact, stopReason);
    }
}
