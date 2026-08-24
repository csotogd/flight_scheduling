namespace Acsp.Core;

/// <summary>A rotation: a cyclic sequence of flight strings operated by one fleet (§3.1.6).</summary>
public sealed class Rotation
{
    public required int FleetId { get; init; }
    /// <summary>Strings in cyclic order (the last connects back to the first).</summary>
    public required List<FlightString> Strings { get; init; }

    /// <summary>Total duration of one cycle in minutes: string spans plus connection times.</summary>
    public long TotalMinutes(Instance inst)
    {
        var p = inst.Period;
        long total = 0;
        for (int i = 0; i < Strings.Count; i++)
        {
            var s = Strings[i];
            var next = Strings[(i + 1) % Strings.Count];
            total += s.ElapsedMinutes(inst);
            total += p.Time(inst.FlightArr(inst.Flights[s.FlightIds[^1]]),
                            inst.FlightDep(inst.Flights[next.FlightIds[0]]));
        }
        return total;
    }

    /// <summary>
    /// Number of airplanes required to operate this rotation periodically. A periodic cyclic
    /// rotation of total length L needs L/N airplanes (L is a multiple of N by construction);
    /// rounded up defensively.
    /// </summary>
    public int AircraftNeeded(Instance inst) =>
        (int)((TotalMinutes(inst) + inst.Period.N - 1) / inst.Period.N);
}

/// <summary>A complete solution of the ACSP.</summary>
public sealed class Solution
{
    public required List<FlightString> SelectedStrings { get; init; }
    public required List<(CargoPath Path, double Tonnes)> Flows { get; init; }
    /// <summary>External flights actually used/booked (relevant with §4.1 fixed external costs).</summary>
    public required HashSet<int> SelectedExternalFlights { get; init; }
    /// <summary>Deliver-all instances: tonnes delivered by contracted external carriers per od
    /// (priced by ExternalRecourse). Empty on classic instances.</summary>
    public List<(int OdId, double Tonnes)> Contracted { get; init; } = [];
    /// <summary>Assembled per-fleet rotations; may be empty if assembly was skipped.</summary>
    public List<Rotation> Rotations { get; init; } = [];
    public bool WithMaintenance { get; init; }

    public double Revenue(Instance inst) =>
        Flows.Sum(f => inst.Ods[f.Path.OdId].Rate * f.Tonnes)
        + Contracted.Sum(c => inst.Ods[c.OdId].Rate * c.Tonnes);

    /// <summary>Cost of contracted external deliveries (deliver-all recourse).</summary>
    public double ContractedCost(Instance inst)
    {
        if (Contracted.Count == 0) return 0;
        var cost = ExternalRecourse.CostPerTonne(inst);
        return Contracted.Sum(c => cost[c.OdId] * c.Tonnes);
    }

    public double VariableCosts(Instance inst) =>
        Flows.Sum(f => (inst.Ods[f.Path.OdId].Rate - f.Path.Margin(inst)) * f.Tonnes);

    public double FixedStringCosts(Instance inst) =>
        SelectedStrings.Sum(s => s.Cost(inst, WithMaintenance));

    public double ExternalFixedCosts(Instance inst) =>
        SelectedExternalFlights.Sum(f => inst.Flights[f].ExternalFixedCost);

    public double AircraftCosts(Instance inst) =>
        Rotations.Sum(r => (double)r.AircraftNeeded(inst) * inst.Fleets[r.FleetId].FixedCostPerAircraft);

    /// <summary>Objective (13): network-wide profit (contracted deliveries earn the od rate
    /// and pay the recourse cost).</summary>
    public double Profit(Instance inst) =>
        Flows.Sum(f => f.Path.Margin(inst) * f.Tonnes)
        + Contracted.Sum(c => inst.Ods[c.OdId].Rate * c.Tonnes) - ContractedCost(inst)
        - FixedStringCosts(inst) - AircraftCosts(inst) - ExternalFixedCosts(inst);
}
