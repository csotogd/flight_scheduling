using Acsp.Core;

namespace Acsp.Solver;

/// <summary>
/// The (string) timeline network of §3.2: departure/arrival events per compatible
/// (fleet, cargo flight) pair, cyclic ground arcs per (fleet, airport), the count time and the
/// count-time crossing coefficients chi.
/// In the string timeline network the arrival event time is the flight arrival plus the minimal
/// maintenance time; in the flight timeline network (FARP-T) it is arrival plus minimal ground time.
/// </summary>
public sealed class TimelineNetwork
{
    public sealed record GroundArc(int Fleet, int Airport, int FromEvent, int ToEvent, int Span, int Chi);

    public Instance Instance { get; }
    public bool WithMaintenance { get; }
    public int CountTime { get; }
    /// <summary>Event ids are dense; DepEvent/ArrEvent map (fleet, flight) to event ids (-1 if incompatible).</summary>
    public int[,] DepEvent { get; }
    public int[,] ArrEvent { get; }
    public int NumEvents { get; private set; }
    public List<GroundArc> GroundArcs { get; } = [];

    public TimelineNetwork(Instance inst, bool withMaintenance)
    {
        Instance = inst;
        WithMaintenance = withMaintenance;
        CountTime = ChooseCountTime(inst);
        DepEvent = new int[inst.Fleets.Length, inst.Flights.Length];
        ArrEvent = new int[inst.Fleets.Length, inst.Flights.Length];
        for (int k = 0; k < inst.Fleets.Length; k++)
            for (int f = 0; f < inst.Flights.Length; f++)
            { DepEvent[k, f] = -1; ArrEvent[k, f] = -1; }
        Build();
    }

    /// <summary>Crossings of the count time by a busy interval [dep, dep + span).</summary>
    public static int Chi(Period p, int countTime, int dep, long span)
    {
        int phi = p.Time(dep, countTime);
        if (span <= phi) return 0;
        return 1 + (int)((span - phi - 1) / p.N);
    }

    public int ChiOfString(FlightString s)
    {
        var inst = Instance;
        int trailing = WithMaintenance
            ? inst.Fleets[s.FleetId].MaintenanceDuration
            : inst.MinGroundTime(inst.FlightDestination(inst.Flights[s.FlightIds[^1]]), s.FleetId);
        long span = s.ElapsedMinutes(inst) + trailing;
        return Chi(inst.Period, CountTime, inst.FlightDep(inst.Flights[s.FlightIds[0]]), span);
    }

    /// <summary>Picks the count time as a departure time when the fewest cargo flights are airborne.</summary>
    private static int ChooseCountTime(Instance inst)
    {
        var p = inst.Period;
        int best = 0, bestAirborne = int.MaxValue;
        foreach (var candidate in inst.CargoFlights.Select(f => inst.FlightDep(f)).Distinct())
        {
            int airborne = inst.CargoFlights.Count(f =>
                p.Time(inst.FlightDep(f), candidate) < inst.FlightDuration(f));
            if (airborne < bestAirborne) { bestAirborne = airborne; best = candidate; }
        }
        return best;
    }

    private void Build()
    {
        var inst = Instance;
        var p = inst.Period;
        // events per (fleet, airport): (time, isArrival, flight)
        var events = new Dictionary<(int Fleet, int Airport), List<(int Time, bool IsArr, int Flight)>>();
        foreach (var f in inst.CargoFlights)
        {
            for (int k = 0; k < inst.Fleets.Length; k++)
            {
                if (!inst.Compatible(k, f.Id)) continue;
                int trailing = WithMaintenance
                    ? inst.Fleets[k].MaintenanceDuration
                    : inst.MinGroundTime(inst.FlightDestination(f), k);
                int depT = inst.FlightDep(f);
                int arrT = p.Wrap(inst.FlightArr(f) + trailing);
                Add(events, (k, inst.FlightOrigin(f)), (depT, false, f.Id));
                Add(events, (k, inst.FlightDestination(f)), (arrT, true, f.Id));
            }
        }

        int nextEvent = 0;
        foreach (var ((k, airport), list) in events)
        {
            // arrivals before departures at equal times (allows arriving cargo to depart)
            var ordered = list.OrderBy(e => e.Time).ThenBy(e => e.IsArr ? 0 : 1).ToList();
            var ids = new int[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
            {
                ids[i] = nextEvent++;
                var (_, isArr, flight) = ordered[i];
                if (isArr) ArrEvent[k, flight] = ids[i];
                else DepEvent[k, flight] = ids[i];
            }
            // cyclic ground arcs; spans must add up to a full period
            int spanSum = 0;
            for (int i = 0; i + 1 < ordered.Count; i++)
            {
                int span = p.Time(ordered[i].Time, ordered[i + 1].Time);
                // equal times give span 0 (correct: no time passes between the two events)
                if (ordered[i].Time == ordered[i + 1].Time) span = 0;
                spanSum += span;
                GroundArcs.Add(new GroundArc(k, airport, ids[i], ids[i + 1], span,
                    ChiOfArc(ordered[i].Time, span)));
            }
            int closingSpan = p.N - spanSum;
            GroundArcs.Add(new GroundArc(k, airport, ids[^1], ids[0], closingSpan,
                ChiOfArc(ordered[^1].Time, closingSpan)));
        }
        NumEvents = nextEvent;
    }

    private int ChiOfArc(int fromTime, int span) =>
        span > 0 && Instance.Period.Time(fromTime, CountTime) < span ? 1 : 0;

    private static void Add<TK, TV>(Dictionary<TK, List<TV>> d, TK key, TV val) where TK : notnull
    {
        if (!d.TryGetValue(key, out var list)) { list = []; d[key] = list; }
        list.Add(val);
    }
}
