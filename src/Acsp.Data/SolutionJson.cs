using System.Text.Json;
using Acsp.Core;
using Acsp.Solver;

namespace Acsp.Data;

/// <summary>
/// Self-contained solution JSON for reporting and the web UI: geography, schedule, rotations,
/// cargo flows, leg loads and the P&amp;L breakdown.
/// </summary>
public static class SolutionJson
{
    public static void Save(Instance inst, BpcResult res, string path, object? design = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(Build(inst, res, design),
            new JsonSerializerOptions { WriteIndented = false }));
    }

    /// <summary>Serializable autonomous-design report (rounds + proposal lifecycle) for the
    /// 'design' property of Build/Save. Shared by the CLI and the web job runner.</summary>
    public static object DesignReport(DesignResult res) => new
    {
        stopReason = res.StopReason,
        baseProfit = res.BaseProfit,
        bestRound = res.BestRound,
        rounds = res.Rounds.Select(r => new
        {
            round = r.Round, profit = Fin(r.Profit), bound = Fin(r.Bound),
            gap = Fin(r.Gap), flights = r.FlightsInModel, added = r.Added,
            flown = r.Flown, evicted = r.Evicted, seconds = Math.Round(r.Seconds, 1),
            note = r.Note,
        }),
        proposals = res.Proposals.Select(t => new
        {
            code = t.Code, route = t.Route, depMinute = t.DepMinute,
            targetPair = t.TargetPair, targetTonnes = t.TargetTonnes, reason = t.Reason,
            addedRound = t.AddedRound, status = t.Status,
            evictedRound = t.EvictedRound < 0 ? (int?)null : t.EvictedRound,
        }),
    };

    private static double? Fin(double v) => double.IsFinite(v) ? Math.Round(v, 4) : null;

    /// <param name="design">Optional autonomous-design report (rounds + proposal lifecycle),
    /// embedded verbatim under the 'design' property.</param>
    public static object Build(Instance inst, BpcResult res, object? design = null)
    {
        var sol = res.Best ?? throw new ArgumentException("no solution");
        var p = inst.Period;

        var legLoad = new double[inst.Legs.Length];
        var legVol = new double[inst.Legs.Length];
        foreach (var (path, tons) in sol.Flows)
            foreach (var lid in path.LegIds)
            {
                legLoad[lid] += tons;
                legVol[lid] += tons * inst.Ods[path.OdId].VolumePerTonne;
            }

        var coveredBy = new int[inst.Flights.Length]; // fleet id or -1
        Array.Fill(coveredBy, -1);
        foreach (var s in sol.SelectedStrings)
            foreach (var f in s.FlightIds)
                coveredBy[f] = s.FleetId;

        var odShipped = new double[inst.Ods.Length];
        foreach (var (path, tons) in sol.Flows) odShipped[path.OdId] += tons;

        // Demand-at-risk diagnostics: which O&Ds have any feasible itinerary at all (full master
        // flight list) and which have one in the selected schedule. Demand is an upper bound in
        // the model (14), so unservable O&Ds never make the model infeasible — they surface here.
        var pricer = new PathPricer(inst);
        var allowAll = PricingRestrictions.AllowAll(inst);
        var inSchedule = PricingRestrictions.AllowAll(inst);
        foreach (var leg in inst.Legs)
        {
            var flight = inst.Flights[leg.FlightId];
            bool available = flight.IsExternal
                ? flight.ExternalFixedCost <= 0 || sol.SelectedExternalFlights.Contains(flight.Id)
                : coveredBy[flight.Id] >= 0;
            if (!available) inSchedule.LegVisible[leg.Id] = false;
        }
        var servable = new bool[inst.Ods.Length];
        var servableInSchedule = new bool[inst.Ods.Length];
        var existDuals = MasterDuals.Zero(inst);
        foreach (var od in inst.Ods)
        {
            existDuals.OdDemand[od.Id] = -1e9; // feasibility probe: any path wins regardless of margin
            servable[od.Id] = pricer.PriceOd(od, existDuals, allowAll) is not null;
            servableInSchedule[od.Id] = servable[od.Id] &&
                pricer.PriceOd(od, existDuals, inSchedule) is not null;
            existDuals.OdDemand[od.Id] = 0;
        }

        return new
        {
            instance = inst.Name,
            withMaintenance = sol.WithMaintenance,
            stats = new
            {
                objective = res.Objective,
                bound = res.Bound,
                gap = res.Gap,
                nodes = res.NodesExplored,
                seconds = res.ElapsedSeconds,
                firstIncumbentObjective = res.FirstIncumbentObjective,
                firstIncumbentSeconds = res.FirstIncumbentSeconds,
                exact = res.Exact,
                stopReason = res.StopReason,
            },
            pnl = new
            {
                revenue = sol.Revenue(inst),
                variableCosts = sol.VariableCosts(inst),
                fixedFlightCosts = sol.FixedStringCosts(inst),
                aircraftCosts = sol.AircraftCosts(inst),
                externalCosts = sol.ExternalFixedCosts(inst),
                profit = sol.Profit(inst),
            },
            airports = inst.Airports.Select(ap => new
            {
                id = ap.Id, code = ap.Code, name = ap.Name, lat = ap.Lat, lon = ap.Lon,
                hub = ap.IsTransferHub,
            }),
            fleets = inst.Fleets.Select(k => new
            {
                id = k.Id, code = k.Code, count = k.Count,
                maxWeight = k.MaxWeight, maxVolume = k.MaxVolume,
            }),
            flights = inst.Flights.Select(f => new
            {
                id = f.Id, code = f.Code,
                kind = f.IsExternal ? "external" : f.IsMandatory ? "mandatory" : "optional",
                selected = f.IsExternal
                    ? (f.ExternalFixedCost <= 0 || sol.SelectedExternalFlights.Contains(f.Id))
                    : coveredBy[f.Id] >= 0,
                fleet = coveredBy[f.Id] >= 0 ? inst.Fleets[coveredBy[f.Id]].Code : null,
                legs = f.LegIds.Select(l => new
                {
                    id = l,
                    from = inst.Legs[l].Origin, to = inst.Legs[l].Destination,
                    dep = inst.Legs[l].Dep, arr = inst.Legs[l].Arr,
                    km = inst.Legs[l].DistanceKm,
                    loadT = Math.Round(legLoad[l], 2),
                    loadM3 = Math.Round(legVol[l], 1),
                    capT = f.IsExternal ? inst.Legs[l].MaxWeight
                        : coveredBy[f.Id] >= 0 ? inst.Fleets[coveredBy[f.Id]].MaxWeight : 0,
                }),
            }),
            rotations = sol.Rotations.Select((r, i) => new
            {
                id = i,
                fleet = inst.Fleets[r.FleetId].Code,
                aircraft = r.AircraftNeeded(inst),
                weeks = r.AircraftNeeded(inst),
                strings = r.Strings.Select(s => new
                {
                    flights = s.FlightIds.Select(f => inst.Flights[f].Code),
                    flightIds = s.FlightIds,
                    dep = inst.FlightDep(inst.Flights[s.FlightIds[0]]),
                    arr = inst.FlightArr(inst.Flights[s.FlightIds[^1]]),
                }),
            }),
            ods = inst.Ods.Select(od => new
            {
                id = od.Id, from = od.Origin, to = od.Destination,
                demandT = od.Weight, shippedT = Math.Round(odShipped[od.Id], 3),
                rate = od.Rate, avail = od.Avail, deadline = od.MaxDeliveryTime,
                servable = servable[od.Id],
                servableInSchedule = servableInSchedule[od.Id],
            }),
            demandAtRisk = new
            {
                unservableOds = servable.Count(s => !s),
                unservableTonnes = Math.Round(inst.Ods.Where(o => !servable[o.Id]).Sum(o => o.Weight), 1),
                notInScheduleOds = inst.Ods.Count(o => servable[o.Id] && !servableInSchedule[o.Id]),
                notInScheduleTonnes = Math.Round(
                    inst.Ods.Where(o => servable[o.Id] && !servableInSchedule[o.Id]).Sum(o => o.Weight), 1),
            },
            flows = sol.Flows.Select(f => new
            {
                od = f.Path.OdId, legs = f.Path.LegIds, tonnes = Math.Round(f.Tonnes, 3),
            }),
            design,
            periodMinutes = p.N,
        };
    }
}
