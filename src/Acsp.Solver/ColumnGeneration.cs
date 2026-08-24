using Acsp.Core;
using Acsp.Solver.Lp;

namespace Acsp.Solver;

public sealed record ColGenOptions
{
    public int MaxStringColumnsPerIteration { get; init; } = 200;
    /// <summary>n_ibc: violated implied bound cuts added per cutting iteration (§8).</summary>
    public int CutsPerIteration { get; init; } = 100;
    public bool EnableCuts { get; init; } = true;
    /// <summary>Solve PRICE-S only every k-th pricing iteration (§9.3.5); 1 = every iteration.</summary>
    public int StringPricingFrequency { get; init; } = 1;
    public int MaxIterations { get; init; } = 10_000;
    public double Eps { get; init; } = 1e-6;
    /// <summary>Exact string pricing (no label limit) — exponential, tests only.</summary>
    public bool ExactStringPricing { get; init; }
    /// <summary>Label limit sigma of PRICE-S (§6.2).</summary>
    public int SigmaMaxLabels { get; init; } = 20;
}

public sealed record ColGenStats(int PricingIterations, int CuttingIterations,
    int PathsAdded, int StringsAdded, int CutsAdded);

/// <summary>
/// The node solution procedure of Fig. 3: repeatedly solve the RMP, price cargo flow paths and
/// flight strings in parallel, and alternate pricing with cutting iterations until no improving
/// column is found.
/// </summary>
public sealed class ColumnGeneration
{
    private readonly Instance _inst;
    private readonly Rmp _rmp;
    private readonly PathPricer _pathPricer;
    private readonly StringPricer _stringPricer;
    private readonly ColGenOptions _opt;

    public ColumnGeneration(Instance inst, Rmp rmp, ColGenOptions? opt = null)
    {
        _inst = inst;
        _rmp = rmp;
        _opt = opt ?? new ColGenOptions();
        _pathPricer = new PathPricer(inst);
        _stringPricer = new StringPricer(inst, rmp.WithMaintenance, rmp.Network.CountTime)
        {
            ExactMode = _opt.ExactStringPricing,
            SigmaMaxLabels = _opt.SigmaMaxLabels,
        };
    }

    /// <param name="DeadlineHit">Column generation was interrupted by the caller's deadline;
    /// Lp.Objective is then NOT a valid dual bound — use DualBound instead.</param>
    /// <param name="DualBound">Valid upper bound on the true node LP: the converged LP value,
    /// or on deadline the Farley bound LP + sum(d_od·rc_od+) + sum(n_k·rc_k+) computed from
    /// the last full pricing pass (approximate when string pricing is label-limited, as
    /// everywhere else with maintenance).</param>
    public sealed record NodeResult(LpResult Lp, ColGenStats Stats, bool DeadlineHit = false,
        double DualBound = double.PositiveInfinity);

    /// <summary>Fired after every pricing iteration: (iteration, lp objective, columns added).</summary>
    public event Action<int, double, int>? IterationProgress;

    /// <summary>
    /// Solves the LP of the current node to (near) optimality via column generation. When
    /// <paramref name="deadline"/> returns true the loop stops early and returns the Farley
    /// bound computed from the pricing pass of that iteration. With <paramref name="gapExtend"/>
    /// the soft deadline is overridden while the convergence gap (best valid bound vs current
    /// LP) is above <paramref name="gapThreshold"/>, until <paramref name="hardDeadline"/>.
    /// </summary>
    public NodeResult SolveNode(PricingRestrictions rest, CancellationToken ct = default,
        Func<bool>? deadline = null, bool gapExtend = false, double gapThreshold = 0.03,
        Func<bool>? hardDeadline = null)
    {
        int pricingIters = 0, cuttingIters = 0, pathsAdded = 0, stringsAdded = 0, cutsAdded = 0;
        var lp = _rmp.SolveLp();
        // tightest valid dual bound seen across the convergence: every full pricing pass
        // yields one (Farley), and an interruption right after a cutting iteration (duals in
        // mid-swing, huge reduced costs) then falls back to the best earlier bound instead
        // of reporting a uselessly wide one
        double bestValidBound = double.PositiveInfinity;

        for (int iter = 0; iter < _opt.MaxIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();
            if (lp.Status == LpStatus.Infeasible) break;

            // ---- pricing iteration: paths and strings in parallel (§5)
            pricingIters++;
            var duals = _rmp.GetDuals(lp);
            bool deadlineHit = deadline?.Invoke() ?? false;
            // on the deadline iteration always run string pricing too: the Farley bound
            // needs reduced costs from both pricers
            bool priceStrings = deadlineHit || (pricingIters - 1) % _opt.StringPricingFrequency == 0;
            var pathTask = Task.Run(() => _pathPricer.Price(duals, rest, _opt.Eps), ct);
            var stringTask = priceStrings
                ? Task.Run(() => _stringPricer.Price(duals, rest, _opt.MaxStringColumnsPerIteration, _opt.Eps), ct)
                : Task.FromResult(new List<StringPricer.PricedString>());
            Task.WaitAll([pathTask, stringTask], ct);

            if (priceStrings && lp.Status == LpStatus.Optimal)
                bestValidBound = Math.Min(bestValidBound,
                    FarleyBound(lp.Objective, pathTask.Result, stringTask.Result));

            // gap extension: past the soft deadline, keep converging while the LP is still
            // provably far from done (bound quality governs, not the clock) — hard cap aside
            if (deadlineHit && gapExtend && !(hardDeadline?.Invoke() ?? false)
                && lp.Status == LpStatus.Optimal && double.IsFinite(bestValidBound))
            {
                double gap = (bestValidBound - lp.Objective) / Math.Max(1, Math.Abs(bestValidBound));
                if (gap > gapThreshold) deadlineHit = false;
            }

            if (deadlineHit)
            {
                IterationProgress?.Invoke(pricingIters, lp.Objective, 0);
                return new NodeResult(lp, new ColGenStats(pricingIters, cuttingIters,
                    pathsAdded, stringsAdded, cutsAdded),
                    DeadlineHit: true, DualBound: bestValidBound);
            }

            int added = 0;
            foreach (var p in pathTask.Result)
                if (_rmp.AddPath(p.Path)) { added++; pathsAdded++; }
            foreach (var s in stringTask.Result)
                if (_rmp.AddString(s.Str)) { added++; stringsAdded++; }

            IterationProgress?.Invoke(pricingIters, lp.Objective, added);
            if (added > 0)
            {
                lp = _rmp.SolveLp();
                continue;
            }
            // exhaust the string pricer before concluding, if it was skipped this iteration
            if (!priceStrings)
            {
                var late = _stringPricer.Price(duals, rest, _opt.MaxStringColumnsPerIteration, _opt.Eps);
                int lateAdded = late.Count(s => _rmp.AddString(s.Str));
                stringsAdded += lateAdded;
                if (lateAdded > 0) { lp = _rmp.SolveLp(); continue; }
            }

            // ---- cutting iteration (§8)
            if (_opt.EnableCuts)
            {
                cuttingIters++;
                var violated = _rmp.SeparateImpliedBoundCuts(lp);
                int taken = 0;
                foreach (var (od, flight, _) in violated.Take(_opt.CutsPerIteration))
                    if (_rmp.AddImpliedBoundCut(od, flight)) taken++;
                if (taken > 0)
                {
                    cutsAdded += taken;
                    lp = _rmp.SolveLp();
                    continue;
                }
            }
            break; // no improving columns and no violated cuts
        }

        return new NodeResult(lp,
            new ColGenStats(pricingIters, cuttingIters, pathsAdded, stringsAdded, cutsAdded),
            DeadlineHit: false,
            DualBound: lp.Status == LpStatus.Optimal
                ? Math.Min(lp.Objective, bestValidBound) : double.PositiveInfinity);
    }

    /// <summary>
    /// Farley/Lagrangian bound at interruption: current RMP value plus the most optimistic
    /// contribution of the columns not yet generated. Each missing path column improves at
    /// most rc_od per tonne and the od carries at most d_od tonnes; each missing string
    /// improves at most rc per unit and fleet k operates at most n_k strings. The best string
    /// overall is always inside the pricer's top-K return, so the global maximum is exact;
    /// per-od path reduced costs are exact (one best path per od). Eps padding covers
    /// columns below the pricing threshold.
    /// </summary>
    private double FarleyBound(double rmpObjective,
        List<PathPricer.PricedPath> paths, List<StringPricer.PricedString> strings)
    {
        // only columns NOT yet in the master count: for columns already inside, LP
        // optimality guarantees true rc <= 0 regardless of the rc a pricer reports
        var bestPathRc = new Dictionary<int, double>();
        foreach (var p in paths)
        {
            if (_rmp.ContainsPath(p.Path)) continue;
            int od = p.Path.OdId;
            if (!bestPathRc.TryGetValue(od, out double rc) || p.ReducedCost > rc)
                bestPathRc[od] = p.ReducedCost;
        }
        double bound = rmpObjective;
        foreach (var od in _inst.Ods)
            bound += od.Weight *
                Math.Max(_opt.Eps, bestPathRc.GetValueOrDefault(od.Id, 0));
        double bestStringRc = 0;
        foreach (var s in strings)
            if (s.ReducedCost > bestStringRc && !_rmp.ContainsString(s.Str))
                bestStringRc = s.ReducedCost;
        foreach (var k in _inst.Fleets)
            bound += k.Count * Math.Max(_opt.Eps, bestStringRc);
        return bound;
    }
}
