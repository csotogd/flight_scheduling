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
    /// <summary>Price each od on its own core (bit-identical results, collected in od
    /// order); off = sequential sweep.</summary>
    public bool ParallelPricing { get; init; } = true;
    /// <summary>Dual stabilization (Wentges smoothing): hand the pricers a blend of the
    /// previous stability center and the fresh duals, so early dual oscillation stops
    /// producing throwaway columns. Mispricing-safe: whenever a smoothed pass finds nothing,
    /// the pass is repeated with the TRUE duals before any conclusion is drawn, and valid
    /// bounds (Farley) are only ever computed from true-dual passes. Off by default —
    /// worthwhile on large instances, neutral-to-slightly-slower on small ones.</summary>
    public bool DualStabilization { get; init; }
    /// <summary>Weight of the stability center in the blend (0 = raw duals).</summary>
    public double StabilizationAlpha { get; init; } = 0.7;
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
        _pathPricer = new PathPricer(inst)
        {
            MaxDegreeOfParallelism = _opt.ParallelPricing ? 0 : 1,
        };
        _stringPricer = new StringPricer(inst, rmp.WithMaintenance, rmp.Network.CountTime)
        {
            ExactMode = _opt.ExactStringPricing,
            SigmaMaxLabels = _opt.SigmaMaxLabels,
        };
    }

    /// <param name="DeadlineHit">Column generation was interrupted by the caller's deadline;
    /// Lp.Objective is then NOT a valid dual bound — use DualBound instead.</param>
    /// <param name="DualBound">Upper bound on the true node LP: a Farley bound whose path
    /// term comes from an uncapped bound pass of PRICE-P (<see cref="PathPricer.PriceBound"/>)
    /// and whose string term is vacuous without maintenance (every feasible string is a
    /// pre-seeded single flight) or a flight-cover aggregation with maintenance.</param>
    /// <param name="BoundCertified">True when every ingredient of DualBound is provably valid:
    /// the bound-pass path sweep completed for every od (label budget not hit, capacity duals
    /// nonnegative) and string pricing was exhaustive — always the case without maintenance,
    /// only under ExactStringPricing with maintenance (the label-limited PRICE-S can miss the
    /// best missing string, so those bounds are estimates).</param>
    public sealed record NodeResult(LpResult Lp, ColGenStats Stats, bool DeadlineHit = false,
        double DualBound = double.PositiveInfinity, bool BoundCertified = false);

    /// <summary>Fired after every pricing iteration: (iteration, lp objective, columns added).</summary>
    public event Action<int, double, int>? IterationProgress;

    /// <summary>Wall seconds of the last pricing sweep / column adds / LP re-solve — the
    /// per-iteration cost breakdown that tells which lever to pull next.</summary>
    public double LastPriceSeconds => _lastPriceSeconds;
    public double LastAddSeconds => _lastAddSeconds;
    public double LastLpSeconds => _lastLpSeconds;
    private double _lastPriceSeconds, _lastAddSeconds, _lastLpSeconds;

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
        MasterDuals? center = null; // dual stabilization center (last true duals seen)
        // cheap per-iteration Farley estimate used ONLY to steer the gap extension: it is
        // built from the capped pricers, which can miss the best missing column, so it is
        // NOT a valid bound — anything returned to the caller goes through CertifiedFarley
        double steerBound = double.PositiveInfinity;
        double finalDual = double.PositiveInfinity;
        bool finalCertified = false;

        for (int iter = 0; iter < _opt.MaxIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();
            if (lp.Status == LpStatus.Infeasible) break;

            // ---- pricing iteration: paths and strings in parallel (§5)
            pricingIters++;
            var trueDuals = _rmp.GetDuals(lp);
            bool deadlineHit = deadline?.Invoke() ?? false;
            // dual stabilization: price against a blend of the stability center and the
            // fresh duals so early oscillation stops producing throwaway columns. The
            // deadline iteration always prices TRUE duals (the Farley bound needs them),
            // and a smoothed pass that finds nothing is repeated with true duals below —
            // the raw prices always have the last word.
            bool smoothed = _opt.DualStabilization && center is not null && !deadlineHit;
            var duals = smoothed
                ? MasterDuals.Blend(center!, trueDuals, _opt.StabilizationAlpha)
                : trueDuals;
            // on the deadline iteration always run string pricing too: the Farley bound
            // needs reduced costs from both pricers
            bool priceStrings = deadlineHit || (pricingIters - 1) % _opt.StringPricingFrequency == 0;
            var swPrice = System.Diagnostics.Stopwatch.StartNew();
            var pathTask = Task.Run(() => _pathPricer.Price(duals, rest, _opt.Eps), ct);
            var stringTask = priceStrings
                ? Task.Run(() => _stringPricer.Price(duals, rest, _opt.MaxStringColumnsPerIteration, _opt.Eps), ct)
                : Task.FromResult(new List<StringPricer.PricedString>());
            Task.WaitAll([pathTask, stringTask], ct);
            _lastPriceSeconds = swPrice.Elapsed.TotalSeconds;

            // valid bounds only ever come from true-dual passes: reduced costs against
            // smoothed prices do not bound the LP the master actually solved
            if (!smoothed && priceStrings && lp.Status == LpStatus.Optimal)
                steerBound = Math.Min(steerBound,
                    SteeringBound(lp.Objective, pathTask.Result, stringTask.Result));

            // gap extension: past the soft deadline, keep converging while the LP is still
            // provably far from done (bound quality governs, not the clock) — hard cap aside
            if (deadlineHit && gapExtend && !(hardDeadline?.Invoke() ?? false)
                && lp.Status == LpStatus.Optimal && double.IsFinite(steerBound))
            {
                double gap = (steerBound - lp.Objective) / Math.Max(1, Math.Abs(steerBound));
                if (gap > gapThreshold) deadlineHit = false;
            }

            if (deadlineHit)
            {
                IterationProgress?.Invoke(pricingIters, lp.Objective, 0);
                // the deadline iteration always priced TRUE duals (never smoothed), so a
                // certified Farley bound is computed right here; the cheap steering
                // estimates accumulated above are never returned to the caller
                var (db, certified) = lp.Status == LpStatus.Optimal
                    ? CertifiedFarley(lp.Objective, trueDuals, rest)
                    : (double.PositiveInfinity, false);
                return new NodeResult(lp, new ColGenStats(pricingIters, cuttingIters,
                    pathsAdded, stringsAdded, cutsAdded),
                    DeadlineHit: true, DualBound: db, BoundCertified: certified);
            }

            center = trueDuals;
            var swAdd = System.Diagnostics.Stopwatch.StartNew();
            int added = 0;
            foreach (var p in pathTask.Result)
                if (_rmp.AddPath(p.Path)) { added++; pathsAdded++; }
            foreach (var s in stringTask.Result)
                if (_rmp.AddString(s.Str)) { added++; stringsAdded++; }
            _lastAddSeconds = swAdd.Elapsed.TotalSeconds;

            IterationProgress?.Invoke(pricingIters, lp.Objective, added);
            if (added > 0)
            {
                var swLp = System.Diagnostics.Stopwatch.StartNew();
                lp = _rmp.SolveLp();
                _lastLpSeconds = swLp.Elapsed.TotalSeconds;
                continue;
            }
            // mispricing: a smoothed pass finding nothing proves nothing about the true
            // prices — reprice with the raw duals before drawing any conclusion
            if (smoothed)
            {
                var truePaths = _pathPricer.Price(trueDuals, rest, _opt.Eps);
                var trueStrings = _stringPricer.Price(trueDuals, rest,
                    _opt.MaxStringColumnsPerIteration, _opt.Eps);
                if (lp.Status == LpStatus.Optimal)
                    steerBound = Math.Min(steerBound,
                        SteeringBound(lp.Objective, truePaths, trueStrings));
                int trueAdded = truePaths.Count(p => _rmp.AddPath(p.Path));
                pathsAdded += trueAdded;
                int trueStringsAdded = trueStrings.Count(s => _rmp.AddString(s.Str));
                stringsAdded += trueStringsAdded;
                if (trueAdded + trueStringsAdded > 0) { lp = _rmp.SolveLp(); continue; }
            }
            // exhaust the string pricer before concluding, if it was skipped this iteration
            // (the mispricing branch above already repriced strings with the true duals)
            if (!priceStrings && !smoothed)
            {
                var late = _stringPricer.Price(trueDuals, rest, _opt.MaxStringColumnsPerIteration, _opt.Eps);
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

            // the capped pricers finding nothing does not prove convergence: one uncapped
            // PRICE-P pass either yields a column whose exact rc (recomputed from the
            // master's own coefficients) improves the LP — then the loop continues — or
            // feeds a certified Farley bound for the final result
            var pathBound = _pathPricer.PriceBound(trueDuals, rest, _opt.Eps);
            int confirmAdded = 0;
            foreach (var cp in pathBound.Paths)
                if (_rmp.TruePathRc(cp.Path, trueDuals) > _opt.Eps && _rmp.AddPath(cp.Path))
                { confirmAdded++; pathsAdded++; }
            if (confirmAdded > 0) { lp = _rmp.SolveLp(); continue; }
            if (lp.Status == LpStatus.Optimal)
                (finalDual, finalCertified) =
                    CertifiedFarley(lp.Objective, trueDuals, rest, pathBound);
            break; // no improving columns and no violated cuts
        }

        return new NodeResult(lp,
            new ColGenStats(pricingIters, cuttingIters, pathsAdded, stringsAdded, cutsAdded),
            DeadlineHit: false, DualBound: finalDual, BoundCertified: finalCertified);
    }

    /// <summary>
    /// Valid Farley/Lagrangian bound: RMP value plus, per od, d_od times an UPPER bound on
    /// the best reduced cost over all feasible missing paths (uncapped bound pass of
    /// PRICE-P, cut duals charged optimistically), plus a string term. Without maintenance
    /// the string term is vacuous: every feasible string is a single flight and
    /// SeedTrivialStrings put all of them into the master, so no string column is missing
    /// (an eps cushion per aircraft stays). With maintenance, missing multi-flight strings
    /// are aggregated through the flight-cover rows — each string covers at least one flight
    /// and sum over strings covering f of x_s &lt;= 1, hence improvement &lt;=
    /// sum_f max_{s covering f} rc_s+/|s| — which stays valid for chi = 0 strings that
    /// consume no fleet-row capacity (the old n_k aggregation did not). Certified only when
    /// the path pass completed everywhere and string pricing was exhaustive
    /// (ExactStringPricing): the label-limited PRICE-S can miss the per-flight maximum.
    /// </summary>
    private (double Bound, bool Certified) CertifiedFarley(double rmpObjective,
        MasterDuals duals, PricingRestrictions rest,
        (List<PathPricer.PricedPath> Paths, bool Complete)? pathBound = null)
    {
        var (bPaths, complete) = pathBound ?? _pathPricer.PriceBound(duals, rest, _opt.Eps);
        bool certified = complete;
        double bound = rmpObjective;
        var rcByOd = new Dictionary<int, double>();
        foreach (var p in bPaths)
            if (!rcByOd.TryGetValue(p.Path.OdId, out double rc) || p.ReducedCost > rc)
                rcByOd[p.Path.OdId] = p.ReducedCost;
        foreach (var od in _inst.Ods)
            // + 1e-9 absorbs the dominance tolerance of the labeling (1e-12 scale)
            bound += od.Weight * Math.Max(_opt.Eps, rcByOd.GetValueOrDefault(od.Id, 0) + 1e-9);

        if (!_rmp.WithMaintenance)
        {
            // FARP-T: strings are single flights, all pre-seeded by SeedTrivialStrings —
            // no string column can be missing; keep the eps cushion per aircraft
            foreach (var k in _inst.Fleets) bound += k.Count * _opt.Eps;
            return (bound, certified);
        }
        var strings = _stringPricer.Price(duals, rest,
            _opt.ExactStringPricing ? int.MaxValue : _opt.MaxStringColumnsPerIteration, _opt.Eps);
        var perFlight = new double[_inst.Flights.Length];
        foreach (var s in strings)
        {
            if (_rmp.ContainsString(s.Str)) continue; // in-master: accounted for by the LP
            double ratio = s.ReducedCost / s.Str.FlightIds.Length;
            foreach (var fid in s.Str.FlightIds)
                if (ratio > perFlight[fid]) perFlight[fid] = ratio;
        }
        foreach (var f in _inst.CargoFlights)
            bound += Math.Max(_opt.Eps, perFlight[f.Id]);
        return (bound, certified && _opt.ExactStringPricing);
    }

    /// <summary>
    /// Cheap per-iteration Farley ESTIMATE from the capped pricing pass — used only to steer
    /// the gap extension. The capped pricers can miss the best missing column (path label
    /// caps, top-K strings, and with maintenance chi = 0 strings consume no fleet-row
    /// capacity, so the n_k aggregation undercounts), hence this value is never returned to
    /// callers; DualBound always comes from CertifiedFarley.
    /// </summary>
    private double SteeringBound(double rmpObjective,
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
