namespace Acsp.Core;

/// <summary>
/// Independent verification of a Solution against all constraints of §2.2.4.
/// Used by tests and after every solve; deliberately shares no logic with the solver.
/// </summary>
public static class FeasibilityChecker
{
    public sealed record Report(List<string> Violations)
    {
        public bool IsFeasible => Violations.Count == 0;
        public override string ToString() =>
            IsFeasible ? "feasible" : string.Join("\n", Violations);
    }

    public static Report Check(Instance inst, Solution sol, double tol = 1e-6)
    {
        var v = new List<string>();
        var p = inst.Period;

        // --- Flight strings & cover (FA-1-COVER, FA-3-COMP, RP-2/3, RP-4/5 via string feasibility)
        var coverCount = new int[inst.Flights.Length];
        foreach (var s in sol.SelectedStrings)
        {
            if (!s.IsFeasible(inst, sol.WithMaintenance, out var why))
                v.Add($"string [{s.Key()}] infeasible: {why}");
            foreach (var f in s.FlightIds) coverCount[f]++;
        }
        foreach (var f in inst.Flights)
        {
            if (f.IsExternal) continue;
            if (f.IsMandatory && coverCount[f.Id] != 1)
                v.Add($"mandatory flight {f.Code} covered {coverCount[f.Id]} times (FA-1-COVER)");
            if (!f.IsMandatory && coverCount[f.Id] > 1)
                v.Add($"optional flight {f.Code} covered {coverCount[f.Id]} times");
        }

        // --- Rotations (RP-1-CYCLE, RP-2-CONN, RP-3-GROUND) and fleet size (FA-2-SIZE)
        if (sol.Rotations.Count > 0)
        {
            var stringsInRotations = sol.Rotations.SelectMany(r => r.Strings).Count();
            if (stringsInRotations != sol.SelectedStrings.Count)
                v.Add($"rotations contain {stringsInRotations} strings but solution has {sol.SelectedStrings.Count}");

            var usage = new int[inst.Fleets.Length];
            foreach (var r in sol.Rotations)
            {
                for (int i = 0; i < r.Strings.Count; i++)
                {
                    var s = r.Strings[i];
                    var next = r.Strings[(i + 1) % r.Strings.Count];
                    if (s.FleetId != r.FleetId || next.FleetId != r.FleetId)
                        v.Add($"rotation of fleet {r.FleetId} contains string of another fleet");
                    var lastFlight = inst.Flights[s.FlightIds[^1]];
                    var nextFlight = inst.Flights[next.FlightIds[0]];
                    if (inst.FlightDestination(lastFlight) != inst.FlightOrigin(nextFlight))
                        v.Add($"rotation not connected: {lastFlight.Code} -> {nextFlight.Code} (RP-2-CONN)");
                    int conn = p.Time(inst.FlightArr(lastFlight), inst.FlightDep(nextFlight));
                    int required = sol.WithMaintenance
                        ? inst.Fleets[r.FleetId].MaintenanceDuration
                        : inst.MinGroundTime(inst.FlightDestination(lastFlight), r.FleetId);
                    if (conn < required)
                        v.Add($"rotation connection {lastFlight.Code}->{nextFlight.Code}: {conn}min < {required}min (RP-3-GROUND)");
                }
                usage[r.FleetId] += r.AircraftNeeded(inst);
            }
            foreach (var k in inst.Fleets)
                if (usage[k.Id] > k.Count)
                    v.Add($"fleet {k.Code}: needs {usage[k.Id]} > {k.Count} aircraft (FA-2-SIZE)");
        }

        // --- Cargo routing
        // Which cargo legs are usable, and with which capacity (fleet of the covering string)
        var legWeightCap = new double[inst.Legs.Length];
        var legVolumeCap = new double[inst.Legs.Length];
        foreach (var leg in inst.Legs)
        {
            var flight = inst.Flights[leg.FlightId];
            if (flight.IsExternal)
            {
                legWeightCap[leg.Id] = leg.MaxWeight;
                legVolumeCap[leg.Id] = leg.MaxVolume;
            }
        }
        foreach (var s in sol.SelectedStrings)
            foreach (var fid in s.FlightIds)
                foreach (var lid in inst.Flights[fid].LegIds)
                {
                    legWeightCap[lid] = inst.Fleets[s.FleetId].MaxWeight;
                    legVolumeCap[lid] = inst.Fleets[s.FleetId].MaxVolume;
                }

        var odShipped = new double[inst.Ods.Length];
        var legWeight = new double[inst.Legs.Length];
        var legVolume = new double[inst.Legs.Length];
        foreach (var (path, tons) in sol.Flows)
        {
            if (tons < -tol) v.Add($"negative flow {tons} on path [{path.Key()}]");
            if (tons <= tol) continue;
            if (!path.IsFeasible(inst, out var why))
                v.Add($"path [{path.Key()}] infeasible: {why} (CR-3..7)");
            odShipped[path.OdId] += tons;
            double vpt = inst.Ods[path.OdId].VolumePerTonne;
            foreach (var lid in path.LegIds)
            {
                var flight = inst.Flights[inst.Legs[lid].FlightId];
                if (!flight.IsExternal && coverCount[flight.Id] == 0)
                    v.Add($"flow on leg {lid} of unselected flight {flight.Code}");
                if (flight.IsExternal && flight.ExternalFixedCost > 0 && !sol.SelectedExternalFlights.Contains(flight.Id))
                    v.Add($"flow on unbooked external flight {flight.Code} (§4.1)");
                legWeight[lid] += tons;
                legVolume[lid] += tons * vpt;
            }
        }
        // capacity/demand comparisons use a tolerance relative to the row scale: LP/MIP solvers
        // enforce rows to their own relative feasibility tolerance, and a row aggregating many
        // flow variables can legitimately sit above its bound by ~cap * 1e-6
        // floor of 1e-4 t (0.1 kg): far below anything operationally meaningful
        // floor of 1e-3 (a kilogram on tonne-scale rows): CPLEX/HiGHS enforce rows to their
        // own relative tolerances, which on large-coefficient rows exceed 1e-4 absolute
        double Tol(double scale) => Math.Max(1e-3, Math.Max(tol, 1e-5) * Math.Max(1, scale));
        foreach (var od in inst.Ods)
            if (odShipped[od.Id] > od.Weight + Tol(od.Weight))
                v.Add($"od {od.Id}: shipped {odShipped[od.Id]:F3} > demand {od.Weight:F3} (CR-1-DEMAND)");
        foreach (var leg in inst.Legs)
        {
            if (legWeight[leg.Id] > legWeightCap[leg.Id] + Tol(legWeightCap[leg.Id]))
                v.Add($"leg {leg.Id}: weight {legWeight[leg.Id]:F3} > cap {legWeightCap[leg.Id]:F3} (CR-2-PAYLOAD)");
            if (legVolumeCap[leg.Id] > 0 && legVolume[leg.Id] > legVolumeCap[leg.Id] + Tol(legVolumeCap[leg.Id]))
                v.Add($"leg {leg.Id}: volume {legVolume[leg.Id]:F3} > cap {legVolumeCap[leg.Id]:F3} (CR-2-PAYLOAD)");
        }

        return new Report(v);
    }
}
