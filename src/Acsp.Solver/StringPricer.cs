using Acsp.Core;

namespace Acsp.Solver;

/// <summary>
/// PRICE-S (§6.2): finds flight strings with positive reduced cost.
/// Without maintenance (FARP-T) strings contain exactly one flight and are enumerated directly.
/// With maintenance, a label-pulling algorithm runs over the DAG G_S spanning n_mnt weeks with
/// resources (flight time, cycles, elapsed time), elementarity, a label limit per node
/// (sigma = 20 by default) and bucket ordering by predecessor flight (§6.2, Grönkvist 2005).
/// </summary>
public sealed class StringPricer
{
    private readonly Instance _inst;
    private readonly bool _withMaintenance;
    private readonly int _countTime;
    private readonly int _nWeeks;
    private readonly int[][] _succFlights; // successor cargo flights by airport connectivity
    private readonly int[][] _predFlights; // predecessor cargo flights by airport connectivity
    public int SigmaMaxLabels { get; set; } = 20;

    /// <summary>
    /// Keeps every label (no bucket ordering, no sigma limit, no dominance). Exponential — only
    /// for exactness tests on tiny instances (§6.2 discusses why production must limit labels).
    /// </summary>
    public bool ExactMode { get; set; }

    public StringPricer(Instance inst, bool withMaintenance, int countTime)
    {
        _inst = inst;
        _withMaintenance = withMaintenance;
        _countTime = countTime;
        int n = inst.Period.N;
        int maxElapsed = inst.Fleets.Max(k => k.MaxElapsedMinutesBetweenMaintenance);
        _nWeeks = withMaintenance ? Math.Min(6, (maxElapsed + n - 1) / n + 1) : 1;

        // successor/predecessor lists by airport connectivity
        var byOrigin = inst.CargoFlights.GroupBy(f => inst.FlightOrigin(f))
            .ToDictionary(g => g.Key, g => g.Select(f => f.Id).ToArray());
        var byDest = inst.CargoFlights.GroupBy(f => inst.FlightDestination(f))
            .ToDictionary(g => g.Key, g => g.Select(f => f.Id).ToArray());
        _succFlights = new int[inst.Flights.Length][];
        _predFlights = new int[inst.Flights.Length][];
        foreach (var f in inst.CargoFlights)
        {
            _succFlights[f.Id] = byOrigin.GetValueOrDefault(inst.FlightDestination(f), []);
            _predFlights[f.Id] = byDest.GetValueOrDefault(inst.FlightOrigin(f), []);
        }
    }

    public sealed record PricedString(FlightString Str, double ReducedCost, int Crossings);

    /// <summary>chi: crossings of the count time by an aircraft busy during [dep, dep + span).</summary>
    public int Chi(int depFirst, long span)
    {
        int phi = _inst.Period.Time(depFirst, _countTime);
        if (span <= phi) return 0;
        return 1 + (int)((span - phi - 1) / _inst.Period.N);
    }

    /// <summary>Reduced cost of a complete string (used by tests and for RMP column data).</summary>
    public double ReducedCost(FlightString s, MasterDuals duals)
    {
        double rc = -s.Cost(_inst, _withMaintenance);
        int k = s.FleetId;
        long span = s.ElapsedMinutes(_inst) + TrailingTime(k, s.FlightIds[^1]);
        int chi = Chi(_inst.FlightDep(_inst.Flights[s.FlightIds[0]]), span);
        rc -= chi * (_inst.Fleets[k].FixedCostPerAircraft + duals.FleetSize[k]);
        rc -= duals.DepBalance[k, s.FlightIds[0]];
        rc += duals.ArrBalance[k, s.FlightIds[^1]];
        foreach (var fid in s.FlightIds)
        {
            rc -= duals.FlightCover[fid];
            foreach (var lid in _inst.Flights[fid].LegIds)
                rc += _inst.Fleets[k].PayloadAtKm(_inst.Legs[lid].DistanceKm) * duals.LegWeight[lid]
                    + _inst.Fleets[k].MaxVolume * duals.LegVolume[lid];
            if (_inst.Flights[fid].IsOptionalCargo)
                foreach (var ((od, fl), pi) in duals.ImpliedBoundCuts)
                    if (fl == fid) rc += pi * _inst.Ods[od].Weight;
        }
        return rc;
    }

    private int TrailingTime(int fleet, int lastFlight) => _withMaintenance
        ? _inst.Fleets[fleet].MaintenanceDuration
        : _inst.MinGroundTime(_inst.FlightDestination(_inst.Flights[lastFlight]), fleet);

    public List<PricedString> Price(MasterDuals duals, PricingRestrictions rest,
        int maxColumns = 200, double eps = 1e-6)
        => _withMaintenance
            ? PriceWithMaintenance(duals, rest, maxColumns, eps)
            : PriceSingleFlights(duals, rest, maxColumns, eps);

    // ---------------------------------------------------------------- FARP-T

    private List<PricedString> PriceSingleFlights(MasterDuals duals, PricingRestrictions rest,
        int maxColumns, double eps)
    {
        var found = new List<PricedString>();
        foreach (var f in _inst.CargoFlights)
        {
            for (int k = 0; k < _inst.Fleets.Length; k++)
            {
                if (!rest.FlightVisibleForFleet[k][f.Id]) continue;
                var s = new FlightString { FleetId = k, FlightIds = [f.Id] };
                double rc = ReducedCost(s, duals);
                if (rc > eps)
                {
                    long span = _inst.FlightDuration(f) + TrailingTime(k, f.Id);
                    found.Add(new PricedString(s, rc, Chi(_inst.FlightDep(f), span)));
                }
            }
        }
        return found.OrderByDescending(x => x.ReducedCost).Take(maxColumns).ToList();
    }

    // ---------------------------------------------------------------- FARP-TS (maintenance)

    private sealed record Label(int Fleet, int Flight, int Week, double Cost,
        int FlightMinutes, int Cycles, long AbsArr, int DepFirst, long AbsDepFirst, Label? Pred);

    private List<PricedString> PriceWithMaintenance(MasterDuals duals, PricingRestrictions rest,
        int maxColumns, double eps)
    {
        var p = _inst.Period;
        var candidates = new List<PricedString>();

        // per-flight static cost contribution for each fleet (excluding chi / balance terms)
        int nf = _inst.Flights.Length, nk = _inst.Fleets.Length;
        var flightGain = new double[nk, nf];
        foreach (var f in _inst.CargoFlights)
            for (int k = 0; k < nk; k++)
            {
                double g = -f.FixedCostByFleet[k] - duals.FlightCover[f.Id];
                foreach (var lid in f.LegIds)
                    g += _inst.Fleets[k].PayloadAtKm(_inst.Legs[lid].DistanceKm) * duals.LegWeight[lid]
                       + _inst.Fleets[k].MaxVolume * duals.LegVolume[lid];
                flightGain[k, f.Id] = g;
            }
        // cut-dual gains per optional flight: sum over ods of pi * d_od
        var cutGain = new double[nf];
        foreach (var ((od, fl), pi) in duals.ImpliedBoundCuts)
            cutGain[fl] += pi * _inst.Ods[od].Weight;

        // nodes (flight, week) in topological order of absolute departure time
        var nodes = new List<(int Flight, int Week)>();
        foreach (var f in _inst.CargoFlights)
            for (int w = 0; w < _nWeeks; w++)
                nodes.Add((f.Id, w));
        nodes.Sort((a, b) =>
            ((long)a.Week * p.N + _inst.FlightDep(_inst.Flights[a.Flight]))
            .CompareTo((long)b.Week * p.N + _inst.FlightDep(_inst.Flights[b.Flight])));

        var labelsAt = new Dictionary<(int, int), List<Label>>();

        void Finish(Label lab)
        {
            var lastFlight = _inst.Flights[lab.Flight];
            var destAp = _inst.Airports[_inst.FlightDestination(lastFlight)];
            if (destAp.MaintenanceHubFor.Length <= lab.Fleet || !destAp.MaintenanceHubFor[lab.Fleet]) return;
            if (!rest.MayEndAfter(lab.Flight)) return;
            long elapsed = lab.AbsArr - lab.AbsDepFirst;
            var fleet = _inst.Fleets[lab.Fleet];
            if (elapsed > fleet.MaxElapsedMinutesBetweenMaintenance) return;
            long span = elapsed + fleet.MaintenanceDuration;
            int chi = Chi(lab.DepFirst, span);
            double mntCost = destAp.MaintenanceCost.Length > lab.Fleet ? destAp.MaintenanceCost[lab.Fleet] : 0;
            double rc = lab.Cost - mntCost - chi * (fleet.FixedCostPerAircraft + duals.FleetSize[lab.Fleet])
                        + duals.ArrBalance[lab.Fleet, lab.Flight];
            if (rc <= eps) return;
            var flights = new List<int>();
            for (var cur = lab; cur is not null; cur = cur.Pred) flights.Add(cur.Flight);
            flights.Reverse();
            candidates.Add(new PricedString(
                new FlightString { FleetId = lab.Fleet, FlightIds = [.. flights] }, rc, chi));
        }

        bool Visited(Label lab, int flight)
        {
            for (var cur = lab; cur is not null; cur = (Label?)cur.Pred)
                if (cur.Flight == flight) return true;
            return false;
        }

        foreach (var (fid, week) in nodes)
        {
            var f = _inst.Flights[fid];
            long absDep = (long)week * p.N + _inst.FlightDep(f);
            long absArr = absDep + _inst.FlightDuration(f);
            var incoming = new List<Label>();

            // source labels (week 0 only): start at a maintenance hub of the fleet
            if (week == 0)
            {
                var origAp = _inst.Airports[_inst.FlightOrigin(f)];
                for (int k = 0; k < nk; k++)
                {
                    if (!rest.FlightVisibleForFleet[k][fid]) continue;
                    if (origAp.MaintenanceHubFor.Length <= k || !origAp.MaintenanceHubFor[k]) continue;
                    var fleet = _inst.Fleets[k];
                    int ft = _inst.FlightFlightTime(f);
                    if (ft > fleet.MaxFlightMinutesBetweenMaintenance) continue;
                    if (f.NumLegs > fleet.MaxCyclesBetweenMaintenance) continue;
                    incoming.Add(new Label(k, fid, week,
                        flightGain[k, fid] + (f.IsOptionalCargo ? cutGain[fid] : 0)
                            - duals.DepBalance[k, fid],
                        ft, f.NumLegs, absArr, _inst.FlightDep(f), absDep, null));
                }
            }

            // pull labels from predecessor flights in the same or previous week
            foreach (var gid in _predFlights[fid])
            {
                var g = _inst.Flights[gid];
                if (!rest.FollowOnAllowed(g.Id, fid)) continue;
                for (int w = Math.Max(0, week - 1); w <= week; w++)
                {
                    if (!labelsAt.TryGetValue((g.Id, w), out var predLabels)) continue;
                    foreach (var lab in predLabels)
                    {
                        if (!rest.FlightVisibleForFleet[lab.Fleet][fid]) continue;
                        var fleet = _inst.Fleets[lab.Fleet];
                        long wait = absDep - lab.AbsArr;
                        if (wait < _inst.MinGroundTime(_inst.FlightOrigin(f), lab.Fleet)) continue;
                        if (wait > p.N) continue; // more than a week idle wastes an aircraft
                        if (Visited(lab, fid)) continue; // elementarity
                        int ft = lab.FlightMinutes + _inst.FlightFlightTime(f);
                        int cyc = lab.Cycles + f.NumLegs;
                        if (ft > fleet.MaxFlightMinutesBetweenMaintenance) continue;
                        if (cyc > fleet.MaxCyclesBetweenMaintenance) continue;
                        long elapsed = absArr - lab.AbsDepFirst;
                        if (elapsed > fleet.MaxElapsedMinutesBetweenMaintenance) continue;
                        incoming.Add(lab with
                        {
                            Flight = fid, Week = week,
                            Cost = lab.Cost + flightGain[lab.Fleet, fid]
                                 + (f.IsOptionalCargo ? cutGain[fid] : 0),
                            FlightMinutes = ft, Cycles = cyc, AbsArr = absArr, Pred = lab,
                        });
                    }
                }
            }

            if (incoming.Count == 0) continue;

            if (ExactMode)
            {
                labelsAt[(fid, week)] = incoming;
                foreach (var lab in incoming) Finish(lab);
                continue;
            }

            // bucket ordering (§6.2): best label per predecessor flight, then top sigma by cost,
            // with a heuristic dominance test on (cost, flight time, cycles, elapsed)
            var kept = incoming
                .GroupBy(l => (l.Fleet, l.Pred?.Flight ?? -1))
                .Select(g => g.OrderByDescending(l => l.Cost).First())
                .OrderByDescending(l => l.Cost)
                .ToList();
            var final = new List<Label>();
            foreach (var lab in kept)
            {
                if (final.Count >= SigmaMaxLabels) break;
                bool dominated = final.Any(ex => ex.Fleet == lab.Fleet
                    && ex.Cost >= lab.Cost - 1e-12 && ex.FlightMinutes <= lab.FlightMinutes
                    && ex.Cycles <= lab.Cycles && ex.AbsArr - ex.AbsDepFirst <= lab.AbsArr - lab.AbsDepFirst);
                if (!dominated) final.Add(lab);
            }
            labelsAt[(fid, week)] = final;
            foreach (var lab in final) Finish(lab);
        }

        return candidates
            .OrderByDescending(c => c.ReducedCost)
            .DistinctBy(c => c.Str.Key())
            .Take(maxColumns)
            .ToList();
    }
}
