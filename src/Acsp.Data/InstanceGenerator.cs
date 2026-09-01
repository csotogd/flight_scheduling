using Acsp.Core;

namespace Acsp.Data;

/// <summary>
/// Generates the synthetic yet realistic problem instances of §9 (Tables 1 and 2).
/// For a given (airline, seed), sets I/II/III share the same flight pool and O&amp;Ds:
///  - Set I:  all mandatory flights + half of the optional pool,
///  - Set II: all mandatory + all optional flights,
///  - Set III: set II flights, but half of the mandatory flights become optional.
/// </summary>
public sealed class InstanceGenerator
{
    private const double VarCostPerTonneKm = 0.035;
    private readonly AirlineProfile _p;
    private readonly int _seed;
    private readonly Random _rng;

    private sealed record LegDraft(string Orig, string Dest, int Dep, int Arr, double DistKm,
        double MaxWeight = 0, double MaxVolume = 0);
    private sealed record FlightDraft(string Code, List<LegDraft> Legs, bool External, bool Mandatory,
        double ExternalFixedCost = 0);
    private sealed record OdDraft(string Orig, string Dest, int Avail, int MaxDelivery,
        double Weight, double VolPerTonne, double Rate);

    private readonly List<AirportInfo> _cargoDests = [];
    private readonly List<AirportInfo> _extDests = [];
    private readonly List<FlightDraft> _mandatoryPool = [];
    private readonly List<FlightDraft> _optionalPool = [];
    private readonly List<FlightDraft> _externalPool = [];
    private readonly List<OdDraft> _ods = [];

    // budget of block hours for routes only the longest-range fleet can fly (feasibility guard):
    // once exhausted, further routes are generated with legs capped at the second-largest range
    private double _topRange, _secondRange, _forcedHours, _forcedBudgetHours;

    public InstanceGenerator(AirlineProfile profile, int seed)
    {
        _p = profile;
        _seed = seed;
        // string.GetHashCode is randomized per process in .NET, so a stable hash is required
        // for instances to be reproducible across runs
        int code = profile.Code.Aggregate(17, (h, c) => h * 31 + c);
        _rng = new Random(code * 1000 + seed);
        _topRange = profile.Fleets.Max(f => f.RangeKm);
        var below = profile.Fleets.Where(f => f.RangeKm < _topRange).ToList();
        _secondRange = below.Count > 0 ? below.Max(f => f.RangeKm) : _topRange;
        var topFleet = profile.Fleets.First(f => f.RangeKm == _topRange);
        _forcedBudgetHours = below.Count > 0 ? 0.22 * topFleet.Count * 168 : double.PositiveInfinity;
        PickDestinations();
        BuildFlightPool();
        BuildExternalFlights();
        BuildOds();
    }

    public static Instance Generate(string airline, int set, int seed) =>
        new InstanceGenerator(AirlineProfile.Get(airline), seed).Build(set);

    public static Instance Generate(AirlineProfile profile, int set, int seed) =>
        new InstanceGenerator(profile, seed).Build(set);

    public Instance Build(int set)
    {
        if (set is < 1 or > 3) throw new ArgumentException("set must be 1, 2 or 3");
        var flights = new List<FlightDraft>();
        int optHalf = _p.OptionalFlightsSetII / 2;
        int mandHalf = _p.MandatoryFlights / 2;
        switch (set)
        {
            case 1:
                flights.AddRange(_mandatoryPool);
                flights.AddRange(_optionalPool.Take(optHalf));
                break;
            case 2:
                flights.AddRange(_mandatoryPool);
                flights.AddRange(_optionalPool);
                break;
            case 3:
                flights.AddRange(_mandatoryPool.Take(mandHalf));
                flights.AddRange(_mandatoryPool.Skip(mandHalf).Select(f => f with { Mandatory = false }));
                flights.AddRange(_optionalPool);
                break;
        }
        flights.AddRange(_externalPool);
        string roman = set switch { 1 => "I", 2 => "II", _ => "III" };
        return Materialize($"{_p.Code}-{roman}-s{_seed}", flights);
    }

    // ---------------------------------------------------------------- destinations

    private void PickDestinations()
    {
        var hubs = _p.HubCodes.Select(AirportDb.Get).ToList();
        double maxRange = _p.Fleets.Max(f => f.RangeKm);

        // Candidate cargo destinations: airports in the profile regions, excluding hubs,
        // ordered by distance to the nearest hub so networks look hub-centric.
        var candidates = AirportDb.All
            .Where(a => _p.Regions.Contains(a.Region) && !_p.HubCodes.Contains(a.Code))
            .OrderBy(a => hubs.Min(h => GreatCircle.Km(h, a)))
            .Where(a => hubs.Min(h => GreatCircle.Km(h, a)) >= 300)
            .ToList();

        _cargoDests.AddRange(hubs);
        foreach (var a in candidates)
        {
            if (_cargoDests.Count >= _p.NumCargoDestinations) break;
            _cargoDests.Add(a);
        }

        if (_p.External is { } ext)
        {
            var extCandidates = _p.External!.Kind == "RFS"
                ? AirportDb.All.Where(a => a.Region == Region.Europe)
                    .Where(a => hubs.Min(h => GreatCircle.Km(h, a)) is > 80 and <= 1500)
                : AirportDb.All.Where(a => !_p.HubCodes.Contains(a.Code));
            _extDests.AddRange(extCandidates
                .Where(a => !_cargoDests.Contains(a))
                .Take(_p.NumExternalDestinations));
        }
    }

    // ---------------------------------------------------------------- cargo flights

    private void BuildFlightPool()
    {
        int total = _p.MandatoryFlights + _p.OptionalFlightsSetII;
        int optHalf = _p.OptionalFlightsSetII / 2;
        FlightDraft? pendingMirror = null;
        for (int i = 0; i < total; i++)
        {
            string code = $"{_p.Code}{i + 1:D4}";
            FlightDraft draft;
            if (pendingMirror is not null)
            {
                draft = pendingMirror with { Code = code };
                pendingMirror = null;
            }
            else
            {
                // inter-hub routes must come in mirrored pairs (flow balance per fleet/airport
                // requires as many departures as arrivals); a pair may not straddle the
                // mandatory/optional boundary or the set-I optional cutoff
                bool allowPair = i + 1 < total
                    && i != _p.MandatoryFlights - 1
                    && i != _p.MandatoryFlights + optHalf - 1;
                (draft, pendingMirror) = BuildRouteOrPair(code, allowPair);
            }
            if (i < _p.MandatoryFlights)
                _mandatoryPool.Add(draft with { Mandatory = true });
            else
                _optionalPool.Add(draft with { Mandatory = false });
        }
    }

    private (FlightDraft Draft, FlightDraft? Mirror) BuildRouteOrPair(string code, bool allowPair)
    {
        double legCap = _forcedHours >= _forcedBudgetHours ? _secondRange : _topRange;
        if (allowPair && _p.HubCodes.Length > 1 && _rng.NextDouble() < _p.InterHubRouteProb)
        {
            var s = AirportDb.Get(_p.HubCodes[_rng.Next(_p.HubCodes.Length)]);
            var e = AirportDb.Get(_p.HubCodes[_rng.Next(_p.HubCodes.Length)]);
            if (e.Code != s.Code && GreatCircle.Km(s, e) <= legCap * 1.9)
            {
                var outbound = TryBuildRoute(code, s, e, legCap, out var route);
                if (outbound is not null && route is not null)
                {
                    // mirror flies the same stops in reverse (out-and-back): identical leg
                    // distances keep the forced-fleet classes balanced per direction
                    var back = new List<AirportInfo>(route);
                    back.Reverse();
                    var mirror = DraftFromRoute(code, back);
                    AccountForcedHours(outbound);
                    AccountForcedHours(mirror);
                    return (outbound, mirror);
                }
            }
        }
        var hub = AirportDb.Get(_p.HubCodes[_rng.Next(_p.HubCodes.Length)]);
        var draft = TryBuildRoute(code, hub, hub, legCap, out _) ?? BuildFallbackRoundTrip(code);
        AccountForcedHours(draft);
        return (draft, null);
    }

    /// <summary>Tracks block hours of routes that only the longest-range fleet can operate.</summary>
    private void AccountForcedHours(FlightDraft d)
    {
        if (d.Legs.Max(l => l.DistKm) <= _secondRange) return;
        _forcedHours += d.Legs.Sum(l => Period.Weekly.Time(l.Dep, l.Arr)) / 60.0;
    }

    /// <summary>Builds one multi-leg route startHub -> stops -> endHub with per-leg range checks.</summary>
    private FlightDraft? TryBuildRoute(string code, AirportInfo startHub, AirportInfo endHub,
        double maxRange, out List<AirportInfo>? routeOut)
    {
        routeOut = null;
        for (int attempt = 0; attempt < 60; attempt++)
        {
            int stops = _rng.Next(_p.MinStops, _p.MaxStops + 1);
            var route = new List<AirportInfo> { startHub };
            var pool = _cargoDests.Where(a => a.Code != startHub.Code && a.Code != endHub.Code).ToList();
            bool ok = true;
            for (int s = 0; s < stops; s++)
            {
                var cur = route[^1];
                // prefer stops near the current position; last stop must reach the end hub
                var near = pool
                    .Where(a => !route.Contains(a))
                    .Where(a => GreatCircle.Km(cur, a) is > 250 && GreatCircle.Km(cur, a) <= maxRange)
                    .Where(a => s < stops - 1 || GreatCircle.Km(a, endHub) <= maxRange)
                    .OrderBy(a => GreatCircle.Km(cur, a))
                    .Take(10)
                    .ToList();
                if (near.Count == 0) { ok = false; break; }
                route.Add(near[_rng.Next(near.Count)]);
            }
            if (!ok) continue;
            if (GreatCircle.Km(route[^1], endHub) > maxRange) continue;
            route.Add(endHub);
            routeOut = route;
            return DraftFromRoute(code, route);
        }
        return null;
    }

    private FlightDraft BuildFallbackRoundTrip(string code)
    {
        double maxRange = _p.Fleets.Max(f => f.RangeKm);
        var hub = AirportDb.Get(_p.HubCodes[_rng.Next(_p.HubCodes.Length)]);
        var near = _cargoDests.Where(a => a.Code != hub.Code && GreatCircle.Km(hub, a) <= maxRange)
            .OrderBy(a => GreatCircle.Km(hub, a)).Take(8).ToList();
        var dest = near[_rng.Next(near.Count)];
        return DraftFromRoute(code, [hub, dest, hub]);
    }

    private FlightDraft DraftFromRoute(string code, List<AirportInfo> route)
    {
        double speed = _p.Fleets.Max(f => f.SpeedKmH); // flight time is fleet-independent (§2.2.2)
        var legs = new List<LegDraft>();
        var period = Period.Weekly;
        int t = _rng.Next(period.N);
        for (int i = 0; i + 1 < route.Count; i++)
        {
            double dist = GreatCircle.Km(route[i], route[i + 1]);
            int block = (int)Math.Round(dist / speed * 60) + 40;
            // night curfew at the destination: postpone departure until the arrival clears
            t += CurfewDelay(route[i + 1], period.Wrap(t + block));
            int dep = period.Wrap(t);
            int arr = period.Wrap(t + block);
            legs.Add(new LegDraft(route[i].Code, route[i + 1].Code, dep, arr, dist));
            t += block + 75 + _rng.Next(60); // ground time at the intermediate stop
        }
        return new FlightDraft(code, legs, External: false, Mandatory: false);
    }

    /// <summary>Minutes to postpone an arrival at this airport so it clears the profile's
    /// night curfew (0 when open; hubs stay open unless CurfewAtHubs).</summary>
    private int CurfewDelay(AirportInfo ap, int arrMinuteOfWeek)
    {
        if (_p.CurfewStart < 0 || _p.CurfewEnd < 0) return 0;
        if (_p.HubCodes.Contains(ap.Code) && !_p.CurfewAtHubs) return 0;
        int m = arrMinuteOfWeek % 1440;
        bool inside = _p.CurfewStart <= _p.CurfewEnd
            ? m >= _p.CurfewStart && m < _p.CurfewEnd
            : m >= _p.CurfewStart || m < _p.CurfewEnd;
        return inside ? (_p.CurfewEnd - m + 1440) % 1440 : 0;
    }

    // ---------------------------------------------------------------- external flights

    private void BuildExternalFlights()
    {
        if (_p.External is not { } ext || _extDests.Count == 0) return;
        var period = Period.Weekly;
        var hubs = _p.HubCodes.Select(AirportDb.Get).ToList();
        for (int i = 0; i < ext.NumFlights; i++)
        {
            var dest = _extDests[i % _extDests.Count];
            var hub = hubs.OrderBy(h => GreatCircle.Km(h, dest)).First();
            bool outbound = _rng.NextDouble() < 0.5;
            var (o, d) = outbound ? (hub, dest) : (dest, hub);
            double dist = GreatCircle.Km(o, d);
            if (dist > ext.MaxRangeKm) continue;
            int block = (int)Math.Round(dist / ext.SpeedKmH * 60) + (ext.Kind == "RFS" ? 30 : 40);
            int dep = _rng.Next(period.N);
            double w = ext.MinWeight + _rng.NextDouble() * (ext.MaxWeight - ext.MinWeight);
            _externalPool.Add(new FlightDraft(
                $"{ext.Kind}{i + 1:D4}",
                [new LegDraft(o.Code, d.Code, dep, period.Wrap(dep + block), dist,
                    MaxWeight: Math.Round(w, 1), MaxVolume: Math.Round(w * ext.VolumePerTonne))],
                External: true, Mandatory: false));
        }
    }

    // ---------------------------------------------------------------- O&Ds

    private void BuildOds()
    {
        var period = Period.Weekly;
        var airports = _cargoDests.Concat(_extDests).ToList();

        // Departure minutes per origin airport over the full flight pool: O&D availability is
        // anchored shortly before an actual departure (shippers tender cargo against the
        // published schedule). The full pool is set-independent, so ODs are identical across sets.
        var depsByAirport = _mandatoryPool.Concat(_optionalPool).Concat(_externalPool)
            .SelectMany(f => f.Legs.Select(l => (l.Orig, l.Dep)))
            .GroupBy(x => x.Orig, x => x.Dep)
            .ToDictionary(g => g.Key, g => g.ToArray());

        var hubs = _p.HubCodes.Select(AirportDb.Get).ToList();
        var raw = new List<OdDraft>();
        double sumTkm = 0;

        if (_p.GravityPairShare > 0)
        {
            // gravity demand: score every ordered station pair with a distance-decayed random
            // draw and keep the top GravityPairShare — near pairs almost always make the cut,
            // far pairs sometimes do, and the share of covered pairs is exact
            var scored = new List<(AirportInfo O, AirportInfo D, double Dist, double Score)>();
            foreach (var o in _cargoDests)
                foreach (var d in _cargoDests)
                {
                    if (o.Code == d.Code) continue;
                    double dist = GreatCircle.Km(o, d);
                    scored.Add((o, d, dist, _rng.NextDouble() * Math.Exp(-dist / 4000.0)));
                }
            int keep = (int)Math.Round(_p.GravityPairShare * scored.Count);
            foreach (var (o, d, dist, _) in scored.OrderByDescending(x => x.Score).Take(keep))
            {
                // weekly recurrence: a lane ships several times per week, near lanes up to
                // daily (BCN-BRU style), far lanes typically once or twice; occurrences are
                // spread evenly across the week, each an independent shipment
                double freqCeiling = 1 + 6 * Math.Exp(-dist / 6000.0);
                int freq = 1 + (int)(freqCeiling * Math.Pow(_rng.NextDouble(), 1.5));
                freq = Math.Min(freq, 7);
                int baseAvail = depsByAirport.TryGetValue(o.Code, out var deps)
                    ? period.Wrap(deps[_rng.Next(deps.Length)] - 60 - _rng.Next(18 * 60))
                    : _rng.Next(period.N);
                for (int occ = 0; occ < freq; occ++)
                {
                    double w = Math.Exp(0.7 + 1.0 * Gaussian());
                    w = Math.Clamp(w, 0.1, 60);
                    double rate = (100 + dist * (0.28 + 0.22 * _rng.NextDouble())) * _p.RateMultiplier;
                    int days = _rng.Next(_p.MinDeliveryDays, _p.MaxDeliveryDays + 1);
                    int avail = period.Wrap(baseAvail + occ * (period.N / freq)
                        + _rng.Next(-120, 121));
                    raw.Add(new OdDraft(o.Code, d.Code, avail, days * 1440,
                        w, 4.5 + 3.5 * _rng.NextDouble(), Math.Round(rate, 2)));
                    sumTkm += w * dist;
                }
            }
            double gscale = _p.RevenueTkmTarget / sumTkm;
            foreach (var od in raw)
                _ods.Add(od with { Weight = Math.Round(Math.Max(0.05, od.Weight * gscale), 3) });
            return;
        }

        for (int i = 0; i < _p.NumOds; i++)
        {
            // dense (integrator) demand: every station originates O&Ds, destinations spread
            // over the whole network — the round-robin origin guarantees full station coverage
            AirportInfo o, d;
            if (_p.DenseDemand)
            {
                o = airports[i % airports.Count];
                do { d = airports[_rng.Next(airports.Count)]; } while (d.Code == o.Code);
            }
            // hub-and-spoke demand: most tonnage moves via a hub gateway, so with high
            // probability one endpoint of the O&D is a hub (single-flight servable)
            else if (_rng.NextDouble() < 0.65)
            {
                var hub = hubs[_rng.Next(hubs.Count)];
                var other = airports[_rng.Next(airports.Count)];
                while (other.Code == hub.Code) other = airports[_rng.Next(airports.Count)];
                (o, d) = _rng.NextDouble() < 0.5 ? (hub, other) : (other, hub);
            }
            else
            {
                o = airports[_rng.Next(airports.Count)];
                do { d = airports[_rng.Next(airports.Count)]; } while (d.Code == o.Code);
            }
            double dist = GreatCircle.Km(o, d);
            // lognormal-ish weight, median ~2t with a heavy tail
            double w = Math.Exp(0.7 + 1.0 * Gaussian());
            w = Math.Clamp(w, 0.1, 60);
            double rate = (100 + dist * (0.28 + 0.22 * _rng.NextDouble())) * _p.RateMultiplier;
            int days = _rng.Next(_p.MinDeliveryDays, _p.MaxDeliveryDays + 1);
            int avail;
            if (depsByAirport.TryGetValue(o.Code, out var deps))
            {
                int dep = deps[_rng.Next(deps.Length)];
                avail = period.Wrap(dep - 60 - _rng.Next(18 * 60)); // 1h..19h before a departure
            }
            else
            {
                avail = _rng.Next(period.N);
            }
            raw.Add(new OdDraft(o.Code, d.Code, avail, days * 1440,
                w, 4.5 + 3.5 * _rng.NextDouble(), Math.Round(rate, 2)));
            sumTkm += w * dist;
        }
        // scale demand weights so total revenue tonne-km hits the Table 1 target
        double scale = _p.RevenueTkmTarget / sumTkm;
        foreach (var od in raw)
            _ods.Add(od with { Weight = Math.Round(Math.Max(0.05, od.Weight * scale), 3) });
    }

    private double Gaussian()
    {
        double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    // ---------------------------------------------------------------- materialization

    private Instance Materialize(string name, List<FlightDraft> flightDrafts)
    {
        int nFleets = _p.Fleets.Length;

        // dense airport ids over all used airports
        var usedCodes = flightDrafts.SelectMany(f => f.Legs.SelectMany(l => new[] { l.Orig, l.Dest }))
            .Concat(_ods.SelectMany(o => new[] { o.Orig, o.Dest }))
            .Distinct().OrderBy(c => c).ToList();
        var idOf = usedCodes.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);

        var airports = usedCodes.Select((code, id) =>
        {
            var info = AirportDb.Get(code);
            bool isHub = _p.HubCodes.Contains(code);
            return new Airport
            {
                Id = id, Code = code, Name = info.Name, Lat = info.Lat, Lon = info.Lon,
                IsTransferHub = isHub,
                MinTransferTime = isHub ? 120 : 0,
                TransferCostPerTonne = isHub ? 25 : 0,
                StorageCostPerTonneHour = isHub ? 0.4 : 0,
                MaintenanceHubFor = Enumerable.Repeat(isHub, nFleets).ToArray(),
                MaintenanceCost = _p.Fleets.Select(f => isHub ? f.MaintenanceCost : 0).ToArray(),
                MinGroundTimeOverride = Enumerable.Repeat(-1, nFleets).ToArray(),
                CurfewStart = isHub && !_p.CurfewAtHubs ? -1 : _p.CurfewStart,
                CurfewEnd = isHub && !_p.CurfewAtHubs ? -1 : _p.CurfewEnd,
            };
        }).ToArray();

        var fleets = _p.Fleets.Select((f, id) => new FleetType
        {
            Id = id, Code = f.Code, Count = f.Count,
            FixedCostPerAircraft = f.FixedPerAircraftWeek,
            MaxWeight = f.MaxWeight, MaxVolume = f.MaxVolume,
            RangeKm = f.RangeKm, RangeMaxKm = f.RangeMaxKm,
            PayloadAtMaxRangeT = f.PayloadAtMaxRangeT, CruiseSpeedKmH = f.SpeedKmH,
            DefaultMinGroundTime = 60,
            MaxCyclesBetweenMaintenance = f.MntCycles,
            MaxFlightMinutesBetweenMaintenance = f.MntFlightMinutes,
            MaxElapsedMinutesBetweenMaintenance = f.MntElapsedMinutes,
            MaintenanceDuration = f.MntDurationMinutes,
        }).ToArray();

        var legs = new List<Leg>();
        var flights = new List<Flight>();
        foreach (var draft in flightDrafts)
        {
            int flightId = flights.Count;
            var legIds = new List<int>();
            foreach (var l in draft.Legs)
            {
                legs.Add(new Leg
                {
                    Id = legs.Count, FlightId = flightId,
                    Origin = idOf[l.Orig], Destination = idOf[l.Dest],
                    Dep = l.Dep, Arr = l.Arr, DistanceKm = l.DistKm,
                    VariableCostPerTonne = Math.Round(l.DistKm * VarCostPerTonneKm, 2),
                    MaxWeight = l.MaxWeight, MaxVolume = l.MaxVolume,
                });
                legIds.Add(legs.Count - 1);
            }
            flights.Add(new Flight
            {
                Id = flightId, Code = draft.Code, LegIds = [.. legIds],
                IsExternal = draft.External, IsMandatory = draft.Mandatory,
                FixedCostByFleet = draft.External
                    ? []
                    : _p.Fleets.Select(f => Math.Round(
                        draft.Legs.Sum(l => l.DistKm * f.FuelCostPerKm) + draft.Legs.Count * f.LandingFee, 2))
                        .ToArray(),
                ExternalFixedCost = draft.ExternalFixedCost,
            });
        }

        var ods = _ods.Select((o, id) => new Od
        {
            Id = id, Origin = idOf[o.Orig], Destination = idOf[o.Dest],
            Avail = o.Avail, MaxDeliveryTime = o.MaxDelivery,
            Weight = o.Weight, Volume = Math.Round(o.Weight * o.VolPerTonne, 3), Rate = o.Rate,
        }).ToArray();

        var inst = new Instance
        {
            Name = name, Period = Period.Weekly, DeliverAll = _p.DeliverAll,
            CargoHandlingMinutes = _p.CargoHandlingMinutes,
            Airports = airports, Fleets = fleets,
            Legs = [.. legs], Flights = [.. flights], Ods = ods,
        };
        inst.Validate();
        return inst;
    }
}
