using Acsp.Core;
using Acsp.Solver;

namespace Acsp.Tests;

/// <summary>Exhaustive enumeration used as ground truth for pricer tests.</summary>
public static class BruteForce
{
    public static List<CargoPath> AllFeasiblePaths(Instance inst, Od od, int maxLegs = 5)
    {
        var result = new List<CargoPath>();
        var stack = new List<int>();

        void Dfs(int lastLeg)
        {
            var last = inst.Legs[lastLeg];
            if (last.Destination == od.Destination)
            {
                var p = new CargoPath { OdId = od.Id, LegIds = [.. stack] };
                if (p.IsFeasible(inst, out _)) result.Add(p);
                return; // delivered; extending past the destination is pointless
            }
            if (stack.Count >= maxLegs) return;
            foreach (var next in inst.Legs)
            {
                if (next.Origin != last.Destination || stack.Contains(next.Id)) continue;
                stack.Add(next.Id);
                var probe = new CargoPath { OdId = od.Id, LegIds = [.. stack] };
                // prune only on hard structural violations; deadline checked at the end
                if (probe.TotalDeliveryTime(inst) <= od.MaxDeliveryTime) Dfs(next.Id);
                stack.RemoveAt(stack.Count - 1);
            }
        }

        foreach (var leg in inst.Legs.Where(l => l.Origin == od.Origin))
        {
            stack.Add(leg.Id);
            Dfs(leg.Id);
            stack.RemoveAt(stack.Count - 1);
        }
        return result;
    }

    /// <summary>Reduced cost of a path per eq. (30) plus cut duals, computed independently.</summary>
    public static double PathReducedCost(Instance inst, CargoPath p, MasterDuals duals)
    {
        var od = inst.Ods[p.OdId];
        double rc = p.Margin(inst) - duals.OdDemand[od.Id];
        foreach (var lid in p.LegIds)
            rc -= duals.LegWeight[lid] + od.VolumePerTonne * duals.LegVolume[lid];
        // one cut charge per distinct optional flight used
        foreach (var fid in p.LegIds.Select(l => inst.Legs[l].FlightId).Distinct())
            if (inst.Flights[fid].IsOptionalCargo &&
                duals.ImpliedBoundCuts.TryGetValue((od.Id, fid), out double pi))
                rc -= pi;
        return rc;
    }

    public static List<FlightString> AllFeasibleStrings(Instance inst, bool withMaintenance, int maxFlights = 4)
    {
        var result = new List<FlightString>();
        foreach (var k in inst.Fleets)
        {
            var stack = new List<int>();
            void Dfs()
            {
                var s = new FlightString { FleetId = k.Id, FlightIds = [.. stack] };
                if (s.IsFeasible(inst, withMaintenance, out _)) result.Add(s);
                if (stack.Count >= maxFlights) return;
                foreach (var f in inst.CargoFlights)
                {
                    if (stack.Contains(f.Id)) continue;
                    stack.Add(f.Id);
                    Dfs();
                    stack.RemoveAt(stack.Count - 1);
                }
            }
            foreach (var f in inst.CargoFlights)
            {
                stack.Add(f.Id);
                Dfs();
                stack.RemoveAt(stack.Count - 1);
            }
        }
        return result;
    }
}
