using Acsp.Core;

namespace Acsp.Solver;

/// <summary>
/// PRICE-P (§6.1): finds cargo flow paths with positive reduced cost by solving a
/// resource-constrained shortest path problem on the leg connection graph G_P with an
/// A*-guided labeling algorithm. Lower bounds on remaining cost and remaining time are
/// precomputed per destination airport with backward Dijkstra runs (duals excluded, hence
/// admissible).
/// </summary>
public sealed class PathPricer
{
    private readonly Instance _inst;
    private readonly record struct Arc(int To, double Cost, int Time, bool Transfer);
    private readonly List<Arc>[] _succ;
    private readonly List<Arc>[] _pred; // reversed arcs, same cost/time
    private readonly Dictionary<int, (double[] Cost, int[] Time)> _bounds = [];
    private readonly int[] _destinations; // distinct od destinations, for bound prewarming
    private readonly int _maxLabelsPerNode;

    /// <summary>Worker threads for the per-od pricing sweep in <see cref="Price"/>.
    /// 0 = one per core (default); 1 = sequential. Results are identical either way —
    /// each od prices independently and the sweep collects them in od order.</summary>
    public int MaxDegreeOfParallelism { get; set; }

    public PathPricer(Instance inst, int maxLabelsPerNode = 64)
    {
        _inst = inst;
        _maxLabelsPerNode = maxLabelsPerNode;
        int n = inst.Legs.Length;
        _succ = new List<Arc>[n];
        _pred = new List<Arc>[n];
        for (int i = 0; i < n; i++) { _succ[i] = []; _pred[i] = []; }

        // legs grouped by origin airport for connection building
        var byOrigin = inst.Legs.GroupBy(l => l.Origin).ToDictionary(g => g.Key, g => g.ToList());
        var p = inst.Period;
        foreach (var l1 in inst.Legs)
        {
            var flight = inst.Flights[l1.FlightId];
            int posInFlight = Array.IndexOf(flight.LegIds, l1.Id);
            if (!byOrigin.TryGetValue(l1.Destination, out var candidates)) continue;
            foreach (var l2 in candidates)
            {
                if (l2.Id == l1.Id) continue;
                if (l1.FlightId == l2.FlightId)
                {
                    // (a) consecutive legs of the same flight: cargo stays on board, no cost
                    if (posInFlight + 1 < flight.LegIds.Length && flight.LegIds[posInFlight + 1] == l2.Id)
                        _succ[l1.Id].Add(new Arc(l2.Id, 0, p.Time(l1.Arr, l2.Dep), Transfer: false));
                }
                else
                {
                    // (b) transfer between flights at a transfer hub (CR-6, CR-7)
                    var ap = inst.Airports[l1.Destination];
                    if (!ap.IsTransferHub) continue;
                    int wait = p.Time(l1.Arr, l2.Dep);
                    if (wait < ap.MinTransferTime) continue;
                    double cost = ap.TransferCostPerTonne + ap.StorageCostPerTonneHour * wait / 60.0;
                    _succ[l1.Id].Add(new Arc(l2.Id, cost, wait, Transfer: true));
                }
            }
        }
        for (int u = 0; u < n; u++)
            foreach (var a in _succ[u])
                _pred[a.To].Add(new Arc(u, a.Cost, a.Time, a.Transfer));
        _destinations = inst.Ods.Select(o => o.Destination).Distinct().ToArray();
    }

    /// <summary>Admissible lower bounds (static cost, exact time) from every leg to a destination airport.</summary>
    private (double[] Cost, int[] Time) BoundsTo(int destAirport)
    {
        if (_bounds.TryGetValue(destAirport, out var cached)) return cached;
        var res = ComputeBoundsTo(destAirport);
        _bounds[destAirport] = res;
        return res;
    }

    /// <summary>Pure bound computation (two backward Dijkstra runs); no shared state touched,
    /// so prewarming may run one destination per core before a parallel pricing sweep.</summary>
    private (double[] Cost, int[] Time) ComputeBoundsTo(int destAirport)
    {
        int n = _inst.Legs.Length;
        var cost = new double[n];
        var time = new int[n];
        Array.Fill(cost, double.PositiveInfinity);
        Array.Fill(time, int.MaxValue);
        var pqC = new PriorityQueue<int, double>();
        var pqT = new PriorityQueue<int, int>();
        foreach (var l in _inst.Legs)
            if (l.Destination == destAirport)
            {
                cost[l.Id] = 0; pqC.Enqueue(l.Id, 0);
                time[l.Id] = 0; pqT.Enqueue(l.Id, 0);
            }
        while (pqC.TryDequeue(out int v, out double dv))
        {
            if (dv > cost[v]) continue;
            foreach (var a in _pred[v])
            {
                // moving backwards: predecessor u then arc to v; remaining after u includes
                // v's own variable cost and the arc cost
                double cand = dv + a.Cost + _inst.Legs[v].VariableCostPerTonne;
                if (cand < cost[a.To]) { cost[a.To] = cand; pqC.Enqueue(a.To, cand); }
            }
        }
        while (pqT.TryDequeue(out int v, out int tv))
        {
            if (tv > time[v]) continue;
            foreach (var a in _pred[v])
            {
                int cand = tv + a.Time + _inst.Legs[v].BlockTime(_inst.Period);
                if (cand < time[a.To]) { time[a.To] = cand; pqT.Enqueue(a.To, cand); }
            }
        }
        return (cost, time);
    }

    private readonly record struct Label(int Leg, double Cost, int Time, int Pred, int Mask = 0);

    public sealed record PricedPath(CargoPath Path, double ReducedCost);

    /// <summary>Total labels a single bound-pass od search may create before aborting as
    /// incomplete (that od's Farley term is then not certified). Guards against pathological
    /// label growth once the per-node cap is lifted.</summary>
    private const int BoundPassLabelBudget = 2_000_000;

    /// <summary>
    /// Prices all O&amp;Ds and returns the best improving path per O&amp;D (reduced cost &gt; eps).
    /// Each od is an independent shortest-path question over the same read-only graph and
    /// prices, so the sweep runs one od per core; results are collected in od order, making
    /// the output bit-identical to the sequential sweep. useDijkstraOnly disables the A*
    /// guidance (for benchmarking, §9.3.4).
    /// </summary>
    public List<PricedPath> Price(MasterDuals duals, PricingRestrictions rest,
        double eps = 1e-6, bool useDijkstraOnly = false)
    {
        Prewarm();
        var results = new PricedPath?[_inst.Ods.Length];
        Parallel.For(0, _inst.Ods.Length, Par(),
            i => results[i] = PriceOd(_inst.Ods[i], duals, rest, eps, useDijkstraOnly));
        var list = new List<PricedPath>();
        foreach (var r in results)
            if (r is not null) list.Add(r);
        return list;
    }

    private ParallelOptions Par() => new()
    {
        MaxDegreeOfParallelism = MaxDegreeOfParallelism > 0
            ? MaxDegreeOfParallelism : Environment.ProcessorCount,
    };

    /// <summary>Builds the per-destination A* bounds for every od destination, one Dijkstra
    /// pair per core. After this, concurrent <see cref="PriceOd"/> calls only READ shared
    /// state — callers running their own parallel od sweeps must prewarm first.</summary>
    public void Prewarm()
    {
        var missing = _destinations.Where(d => !_bounds.ContainsKey(d)).ToArray();
        if (missing.Length == 0) return;
        var computed = new (double[], int[])[missing.Length];
        Parallel.For(0, missing.Length, Par(), i => computed[i] = ComputeBoundsTo(missing[i]));
        for (int i = 0; i < missing.Length; i++) _bounds[missing[i]] = computed[i];
    }

    /// <summary>
    /// Bound pass over every od: PRICE-P with the per-node label cap lifted and
    /// implied-bound-cut duals charged exactly once per distinct flight (positive duals via
    /// a per-label cut bitmask that also sharpens dominance; nonpositive or overflow duals
    /// charged optimistically). For every concrete path the computed cost is at most the
    /// master's cost of that column (Rmp.AddPath), so the returned per-od reduced cost is an
    /// UPPER bound on the true best reduced cost over all feasible paths of that od —
    /// exactly what a valid Farley term needs — and coincides with it in the ordinary case
    /// (no cut-dual overflow, no reentry). Complete is false when some od aborted on the
    /// label budget or a capacity dual came back negative (the A* guidance is then not
    /// provably admissible) — the caller must treat the resulting bound as NOT certified.
    /// </summary>
    public (List<PricedPath> Paths, bool Complete) PriceBound(MasterDuals duals,
        PricingRestrictions rest, double eps = 1e-6)
    {
        bool complete = true;
        foreach (var leg in _inst.Legs)
            if (duals.LegWeight[leg.Id] < -1e-9 || duals.LegVolume[leg.Id] < -1e-9)
            { complete = false; break; }
        Prewarm();
        var results = new PricedPath?[_inst.Ods.Length];
        var odComplete = new bool[_inst.Ods.Length];
        Parallel.For(0, _inst.Ods.Length, Par(),
            i => results[i] = PriceOdCore(_inst.Ods[i], duals, rest, eps,
                useDijkstraOnly: false, boundPass: true, out odComplete[i]));
        var list = new List<PricedPath>();
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i] is not null) list.Add(results[i]!);
            complete &= odComplete[i];
        }
        return (list, complete);
    }

    public PricedPath? PriceOd(Od od, MasterDuals duals, PricingRestrictions rest,
        double eps = 1e-6, bool useDijkstraOnly = false)
        => PriceOdCore(od, duals, rest, eps, useDijkstraOnly, boundPass: false, out _);

    private PricedPath? PriceOdCore(Od od, MasterDuals duals, PricingRestrictions rest,
        double eps, bool useDijkstraOnly, bool boundPass, out bool complete)
    {
        complete = true;
        var p = _inst.Period;
        var (hCost, hTime) = BoundsTo(od.Destination);
        double target = od.Rate - duals.OdDemand[od.Id]; // must exceed path cost + eps
        // cargo handling: unloading eats into the delivery window at the destination end,
        // loading delays the earliest catchable departure at the origin end (see Push/source)
        int handling = _inst.CargoHandlingMinutes;
        int deadline = od.MaxDeliveryTime - handling;

        double NodeCost(Leg leg)
        {
            double c = leg.VariableCostPerTonne + duals.LegWeight[leg.Id]
                       + od.VolumePerTonne * duals.LegVolume[leg.Id];
            return c;
        }
        // bound pass: the master charges a path pi once per DISTINCT flight (Rmp.AddPath),
        // while the labeling sees flight entries. Positive duals are charged exactly once via
        // a per-label bitmask over the od's cut flights (a keeper label then only dominates a
        // newcomer when it has already charged a SUPERSET of the newcomer's cuts — any
        // completion costs it no more); a nonpositive dual would need the opposite inclusion,
        // so it is credited per entry, which can only over-credit reentry paths — the rc
        // stays an upper bound. Beyond 30 tracked cuts the excess is charged 0 (optimistic).
        var cutBit = new Dictionary<int, int>(); // flight id -> bit index (pi > 0 only)
        var cutPi = new List<double>();
        if (boundPass)
            foreach (var ((odId, fid), pi) in duals.ImpliedBoundCuts)
                if (odId == od.Id && pi > 0 && cutPi.Count < 30)
                { cutBit[fid] = cutPi.Count; cutPi.Add(pi); }

        (double Cost, int Mask) Enter(Leg leg, int mask)
        {
            var f = _inst.Flights[leg.FlightId];
            if (!f.IsOptionalCargo) return (0, mask);
            if (!duals.ImpliedBoundCuts.TryGetValue((od.Id, f.Id), out double pi))
                return (0, mask);
            if (!boundPass) return (pi, mask);
            if (cutBit.TryGetValue(f.Id, out int bi))
                return (mask & (1 << bi)) != 0 ? (0.0, mask) : (cutPi[bi], mask | (1 << bi));
            return (Math.Min(pi, 0), mask);
        }

        var labels = new List<Label>();
        var byNode = new Dictionary<int, List<int>>();
        var pq = new PriorityQueue<int, double>();
        double bestCost = double.PositiveInfinity;
        int bestLabel = -1;

        void Push(Label lab)
        {
            if (lab.Time > deadline) return;
            if (hCost[lab.Leg] == double.PositiveInfinity) return;
            if (hTime[lab.Leg] != int.MaxValue && lab.Time + hTime[lab.Leg] > deadline && _inst.Legs[lab.Leg].Destination != od.Destination) return;
            // pareto dominance at the node
            if (!byNode.TryGetValue(lab.Leg, out var list)) { list = []; byNode[lab.Leg] = list; }
            foreach (var idx in list)
            {
                var ex = labels[idx];
                if (ex.Cost <= lab.Cost + 1e-12 && ex.Time <= lab.Time
                    && (ex.Mask & lab.Mask) == lab.Mask) return;
            }
            list.RemoveAll(idx => labels[idx].Cost >= lab.Cost - 1e-12 && labels[idx].Time >= lab.Time
                && (lab.Mask & labels[idx].Mask) == labels[idx].Mask);
            if (!boundPass && list.Count >= _maxLabelsPerNode) return;
            labels.Add(lab);
            list.Add(labels.Count - 1);
            double h = useDijkstraOnly ? 0 : hCost[lab.Leg];
            pq.Enqueue(labels.Count - 1, lab.Cost + h);
        }

        // source labels: legs departing from the od origin
        foreach (var leg in _inst.Legs)
        {
            if (leg.Origin != od.Origin || !rest.LegVisible[leg.Id]) continue;
            int wait = p.Time(od.Avail, leg.Dep);
            if (wait < handling) wait += p.N; // still loading: catch next week's departure
            int t = wait + leg.BlockTime(p);
            var (ec, m) = Enter(leg, 0);
            Push(new Label(leg.Id, NodeCost(leg) + ec, t, -1, m));
        }

        while (pq.TryDequeue(out int li, out double prio))
        {
            if (boundPass && labels.Count > BoundPassLabelBudget) { complete = false; break; }
            var lab = labels[li];
            if (prio >= bestCost) break;             // A*: cannot improve anymore
            if (prio >= target - eps) break;         // no positive reduced cost reachable
            var leg = _inst.Legs[lab.Leg];
            if (leg.Destination == od.Destination)
            {
                if (lab.Cost < bestCost) { bestCost = lab.Cost; bestLabel = li; }
                continue;
            }
            foreach (var arc in _succ[lab.Leg])
            {
                if (!rest.LegVisible[arc.To]) continue;
                var next = _inst.Legs[arc.To];
                var (ec, m) = arc.Transfer ? Enter(next, lab.Mask) : (0.0, lab.Mask);
                double c = lab.Cost + arc.Cost + NodeCost(next) + ec;
                int t = lab.Time + arc.Time + next.BlockTime(p);
                Push(new Label(arc.To, c, t, li, m));
            }
        }

        if (bestLabel < 0) return null;
        double rc = target - bestCost;
        if (rc <= eps) return null;

        var legIds = new List<int>();
        for (int cur = bestLabel; cur >= 0; cur = labels[cur].Pred)
            legIds.Add(labels[cur].Leg);
        legIds.Reverse();
        return new PricedPath(new CargoPath { OdId = od.Id, LegIds = [.. legIds] }, rc);
    }
}
