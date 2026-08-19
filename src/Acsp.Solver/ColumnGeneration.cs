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

    public sealed record NodeResult(LpResult Lp, ColGenStats Stats);

    /// <summary>Fired after every pricing iteration: (iteration, lp objective, columns added).</summary>
    public event Action<int, double, int>? IterationProgress;

    /// <summary>Solves the LP of the current node to (near) optimality via column generation.</summary>
    public NodeResult SolveNode(PricingRestrictions rest, CancellationToken ct = default)
    {
        int pricingIters = 0, cuttingIters = 0, pathsAdded = 0, stringsAdded = 0, cutsAdded = 0;
        var lp = _rmp.SolveLp();

        for (int iter = 0; iter < _opt.MaxIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();
            if (lp.Status == LpStatus.Infeasible) break;

            // ---- pricing iteration: paths and strings in parallel (§5)
            pricingIters++;
            var duals = _rmp.GetDuals(lp);
            bool priceStrings = (pricingIters - 1) % _opt.StringPricingFrequency == 0;
            var pathTask = Task.Run(() => _pathPricer.Price(duals, rest, _opt.Eps), ct);
            var stringTask = priceStrings
                ? Task.Run(() => _stringPricer.Price(duals, rest, _opt.MaxStringColumnsPerIteration, _opt.Eps), ct)
                : Task.FromResult(new List<StringPricer.PricedString>());
            Task.WaitAll([pathTask, stringTask], ct);

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
            new ColGenStats(pricingIters, cuttingIters, pathsAdded, stringsAdded, cutsAdded));
    }
}
