namespace Acsp.Core;

/// <summary>
/// An (augmented) flight string (§3.1.9): a maintenance-feasible sequence of connected cargo
/// flights conducted by one fleet, starting and ending at maintenance hubs for that fleet,
/// with the minimal maintenance time attached after the last flight.
/// </summary>
public sealed class FlightString
{
    public required int FleetId { get; init; }
    public required int[] FlightIds { get; init; }

    /// <summary>cstr_s: fixed costs of all flights plus the maintenance stop at the final airport.</summary>
    public double Cost(Instance inst, bool withMaintenance)
    {
        double c = 0;
        foreach (var fid in FlightIds) c += inst.Flights[fid].FixedCostByFleet[FleetId];
        if (withMaintenance)
        {
            var dest = inst.Airports[inst.FlightDestination(inst.Flights[FlightIds[^1]])];
            if (dest.MaintenanceCost.Length > FleetId) c += dest.MaintenanceCost[FleetId];
        }
        return c;
    }

    /// <summary>Total cycles (take-offs) of the string: number of legs over all flights.</summary>
    public int Cycles(Instance inst) => FlightIds.Sum(f => inst.Flights[f].NumLegs);

    /// <summary>Total flight minutes of the string.</summary>
    public int FlightMinutes(Instance inst) => FlightIds.Sum(f => inst.FlightFlightTime(inst.Flights[f]));

    /// <summary>Elapsed minutes: sum of flight durations and connection times, per §3.1.9.</summary>
    public int ElapsedMinutes(Instance inst)
    {
        var p = inst.Period;
        int t = 0;
        for (int i = 0; i < FlightIds.Length; i++)
        {
            var f = inst.Flights[FlightIds[i]];
            t += inst.FlightDuration(f);
            if (i + 1 < FlightIds.Length)
                t += p.Time(inst.FlightArr(f), inst.FlightDep(inst.Flights[FlightIds[i + 1]]));
        }
        return t;
    }

    /// <summary>
    /// Feasibility per §3.1.9. With withMaintenance=false the string degenerates to the FARP-T
    /// case (single flight, no maintenance-hub or resource requirements).
    /// </summary>
    public bool IsFeasible(Instance inst, bool withMaintenance, out string? reason)
    {
        var p = inst.Period;
        var k = inst.Fleets[FleetId];
        if (FlightIds.Length == 0) { reason = "empty string"; return false; }

        foreach (var fid in FlightIds)
        {
            var f = inst.Flights[fid];
            if (f.IsExternal) { reason = $"flight {f.Code} is external"; return false; }
            if (!inst.Compatible(FleetId, fid)) { reason = $"flight {f.Code} incompatible with fleet {k.Code}"; return false; }
        }
        for (int i = 0; i + 1 < FlightIds.Length; i++)
        {
            var a = inst.Flights[FlightIds[i]];
            var b = inst.Flights[FlightIds[i + 1]];
            if (inst.FlightDestination(a) != inst.FlightOrigin(b))
            { reason = $"flights {a.Code}->{b.Code} not connected"; return false; }
            if (p.Time(inst.FlightArr(a), inst.FlightDep(b)) < inst.MinGroundTime(inst.FlightDestination(a), FleetId))
            { reason = $"ground time too short before {b.Code}"; return false; }
        }
        if (withMaintenance)
        {
            if (FlightIds.Distinct().Count() != FlightIds.Length) { reason = "flight repeated in string"; return false; }
            var orig = inst.Airports[inst.FlightOrigin(inst.Flights[FlightIds[0]])];
            var dest = inst.Airports[inst.FlightDestination(inst.Flights[FlightIds[^1]])];
            if (orig.MaintenanceHubFor.Length <= FleetId || !orig.MaintenanceHubFor[FleetId])
            { reason = $"origin {orig.Code} is not a maintenance hub for {k.Code}"; return false; }
            if (dest.MaintenanceHubFor.Length <= FleetId || !dest.MaintenanceHubFor[FleetId])
            { reason = $"destination {dest.Code} is not a maintenance hub for {k.Code}"; return false; }
            if (Cycles(inst) > k.MaxCyclesBetweenMaintenance) { reason = "max cycles exceeded"; return false; }
            if (FlightMinutes(inst) > k.MaxFlightMinutesBetweenMaintenance) { reason = "max flight time exceeded"; return false; }
            if (ElapsedMinutes(inst) > k.MaxElapsedMinutesBetweenMaintenance) { reason = "max elapsed time exceeded"; return false; }
        }
        else if (FlightIds.Length != 1)
        { reason = "FARP-T strings must contain exactly one flight"; return false; }

        reason = null;
        return true;
    }

    public string Key() => $"{FleetId}:{string.Join(',', FlightIds)}";
}
