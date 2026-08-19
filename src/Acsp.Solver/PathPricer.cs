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
    private readonly int _maxLabelsPerNode;

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
    }

    /// <summary>Admissible lower bounds (static cost, exact time) from every leg to a destination airport.</summary>
    private (double[] Cost, int[] Time) BoundsTo(int destAirport)
    {
        if (_bounds.TryGetValue(destAirport, out var cached)) return cached;
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
        var res = (cost, time);
        _bounds[destAirport] = res;
        return res;
    }

    private readonly record struct Label(int Leg, double Cost, int Time, int Pred);

    public sealed record PricedPath(CargoPath Path, double ReducedCost);

    /// <summary>
    /// Prices all O&amp;Ds and returns the best improving path per O&amp;D (reduced cost &gt; eps).
    /// useDijkstraOnly disables the A* guidance (for benchmarking, §9.3.4).
    /// </summary>
    public List<PricedPath> Price(MasterDuals duals, PricingRestrictions rest,
        double eps = 1e-6, bool useDijkstraOnly = false)
    {
        var result = new List<PricedPath>();
        foreach (var od in _inst.Ods)
        {
            var best = PriceOd(od, duals, rest, eps, useDijkstraOnly);
            if (best is not null) result.Add(best);
        }
        return result;
    }

    public PricedPath? PriceOd(Od od, MasterDuals duals, PricingRestrictions rest,
        double eps = 1e-6, bool useDijkstraOnly = false)
    {
        var p = _inst.Period;
        var (hCost, hTime) = BoundsTo(od.Destination);
        double target = od.Rate - duals.OdDemand[od.Id]; // must exceed path cost + eps

        double NodeCost(Leg leg)
        {
            double c = leg.VariableCostPerTonne + duals.LegWeight[leg.Id]
                       + od.VolumePerTonne * duals.LegVolume[leg.Id];
            return c;
        }
        double EntryCost(Leg leg)
        {
            var f = _inst.Flights[leg.FlightId];
            if (f.IsOptionalCargo &&
                duals.ImpliedBoundCuts.TryGetValue((od.Id, f.Id), out double pi))
                return pi;
            return 0;
        }

        var labels = new List<Label>();
        var byNode = new Dictionary<int, List<int>>();
        var pq = new PriorityQueue<int, double>();
        double bestCost = double.PositiveInfinity;
        int bestLabel = -1;

        void Push(Label lab)
        {
            if (lab.Time > od.MaxDeliveryTime) return;
            if (hCost[lab.Leg] == double.PositiveInfinity) return;
            if (hTime[lab.Leg] != int.MaxValue && lab.Time + hTime[lab.Leg] > od.MaxDeliveryTime && _inst.Legs[lab.Leg].Destination != od.Destination) return;
            // pareto dominance at the node
            if (!byNode.TryGetValue(lab.Leg, out var list)) { list = []; byNode[lab.Leg] = list; }
            foreach (var idx in list)
            {
                var ex = labels[idx];
                if (ex.Cost <= lab.Cost + 1e-12 && ex.Time <= lab.Time) return;
            }
            list.RemoveAll(idx => labels[idx].Cost >= lab.Cost - 1e-12 && labels[idx].Time >= lab.Time);
            if (list.Count >= _maxLabelsPerNode) return;
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
            int t = wait + leg.BlockTime(p);
            Push(new Label(leg.Id, NodeCost(leg) + EntryCost(leg), t, -1));
        }

        while (pq.TryDequeue(out int li, out double prio))
        {
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
                double c = lab.Cost + arc.Cost + NodeCost(next) + (arc.Transfer ? EntryCost(next) : 0);
                int t = lab.Time + arc.Time + next.BlockTime(p);
                Push(new Label(arc.To, c, t, li));
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
