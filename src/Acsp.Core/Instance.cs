namespace Acsp.Core;

/// <summary>A complete ACSP problem instance (§2.2.1).</summary>
public sealed class Instance
{
    public required string Name { get; init; }
    public required Period Period { get; init; }
    public required Airport[] Airports { get; init; }
    public required FleetType[] Fleets { get; init; }
    public required Leg[] Legs { get; init; }
    public required Flight[] Flights { get; init; }
    public required Od[] Ods { get; init; }

    private bool[,]? _comp;

    public IEnumerable<Flight> CargoFlights => Flights.Where(f => !f.IsExternal);
    public IEnumerable<Flight> ExternalFlights => Flights.Where(f => f.IsExternal);
    public IEnumerable<Flight> MandatoryFlights => Flights.Where(f => !f.IsExternal && f.IsMandatory);
    public IEnumerable<Flight> OptionalFlights => Flights.Where(f => f.IsOptionalCargo);
    public IEnumerable<Leg> CargoLegs => Legs.Where(l => !Flights[l.FlightId].IsExternal);
    public IEnumerable<Leg> ExternalLegs => Legs.Where(l => Flights[l.FlightId].IsExternal);

    /// <summary>gtmin_{a,k} in minutes.</summary>
    public int MinGroundTime(int airportId, int fleetId)
    {
        var ov = Airports[airportId].MinGroundTimeOverride;
        int v = ov.Length > fleetId ? ov[fleetId] : -1;
        return v >= 0 ? v : Fleets[fleetId].DefaultMinGroundTime;
    }

    /// <summary>comp_{k,f} (§3.1.5): fleet k can conduct cargo flight f.</summary>
    public bool Compatible(int fleetId, int flightId)
    {
        _comp ??= ComputeCompatibility();
        return _comp[fleetId, flightId];
    }

    private bool[,] ComputeCompatibility()
    {
        var comp = new bool[Fleets.Length, Flights.Length];
        foreach (var f in Flights)
        {
            if (f.IsExternal) continue;
            foreach (var k in Fleets)
            {
                if (f.ForbiddenFleets is { } forb && forb.Length > k.Id && forb[k.Id]) continue;
                bool ok = true;
                for (int i = 0; i < f.LegIds.Length && ok; i++)
                {
                    var leg = Legs[f.LegIds[i]];
                    if (leg.DistanceKm > k.RangeKm) ok = false;
                    // ground time between consecutive legs of the flight
                    if (ok && i + 1 < f.LegIds.Length)
                    {
                        var next = Legs[f.LegIds[i + 1]];
                        if (Period.Time(leg.Arr, next.Dep) < MinGroundTime(leg.Destination, k.Id))
                            ok = false;
                    }
                }
                comp[k.Id, f.Id] = ok;
            }
        }
        return comp;
    }

    public Leg FirstLeg(Flight f) => Legs[f.LegIds[0]];
    public Leg LastLeg(Flight f) => Legs[f.LegIds[^1]];
    /// <summary>orig_f / dep_f.</summary>
    public int FlightOrigin(Flight f) => FirstLeg(f).Origin;
    public int FlightDep(Flight f) => FirstLeg(f).Dep;
    /// <summary>dest_f / arr_f.</summary>
    public int FlightDestination(Flight f) => LastLeg(f).Destination;
    public int FlightArr(Flight f) => LastLeg(f).Arr;
    /// <summary>dur_f: total duration in minutes.</summary>
    public int FlightDuration(Flight f) => Period.Time(FlightDep(f), FlightArr(f));
    /// <summary>ft_f: sum of leg flight times.</summary>
    public int FlightFlightTime(Flight f) => f.LegIds.Sum(l => Legs[l].FlightTime(Period));

    /// <summary>Basic structural validation of the raw data; throws on inconsistency.</summary>
    public void Validate()
    {
        for (int i = 0; i < Airports.Length; i++)
            if (Airports[i].Id != i) throw new InvalidDataException($"Airport id mismatch at {i}");
        for (int i = 0; i < Fleets.Length; i++)
            if (Fleets[i].Id != i) throw new InvalidDataException($"Fleet id mismatch at {i}");
        for (int i = 0; i < Legs.Length; i++)
            if (Legs[i].Id != i) throw new InvalidDataException($"Leg id mismatch at {i}");
        for (int i = 0; i < Flights.Length; i++)
            if (Flights[i].Id != i) throw new InvalidDataException($"Flight id mismatch at {i}");
        for (int i = 0; i < Ods.Length; i++)
            if (Ods[i].Id != i) throw new InvalidDataException($"Od id mismatch at {i}");

        foreach (var f in Flights)
        {
            if (f.LegIds.Length == 0) throw new InvalidDataException($"Flight {f.Code} has no legs");
            for (int i = 0; i < f.LegIds.Length; i++)
            {
                var leg = Legs[f.LegIds[i]];
                if (leg.FlightId != f.Id)
                    throw new InvalidDataException($"Leg {leg.Id} does not point back to flight {f.Code}");
                if (i + 1 < f.LegIds.Length)
                {
                    var next = Legs[f.LegIds[i + 1]];
                    if (leg.Destination != next.Origin)
                        throw new InvalidDataException($"Flight {f.Code}: legs {leg.Id}->{next.Id} not connected");
                }
                if (leg.Dep < 0 || leg.Dep >= Period.N || leg.Arr < 0 || leg.Arr >= Period.N)
                    throw new InvalidDataException($"Leg {leg.Id}: dep/arr outside [0, N)");
            }
            if (!f.IsExternal && f.FixedCostByFleet.Length != Fleets.Length)
                throw new InvalidDataException($"Flight {f.Code}: FixedCostByFleet must have one entry per fleet");
        }
        foreach (var leg in ExternalLegs)
            if (leg.MaxWeight <= 0)
                throw new InvalidDataException($"External leg {leg.Id} needs positive MaxWeight");
        foreach (var od in Ods)
        {
            if (od.Weight <= 0 || od.Rate < 0) throw new InvalidDataException($"Od {od.Id}: bad weight/rate");
            if (od.Origin == od.Destination) throw new InvalidDataException($"Od {od.Id}: origin == destination");
        }
        foreach (var k in Fleets)
            if (Airports.All(a => a.MaintenanceHubFor.Length <= k.Id || !a.MaintenanceHubFor[k.Id]))
                if (k.MaxElapsedMinutesBetweenMaintenance != int.MaxValue)
                    throw new InvalidDataException($"Fleet {k.Code} has maintenance limits but no maintenance hub");
    }
}
