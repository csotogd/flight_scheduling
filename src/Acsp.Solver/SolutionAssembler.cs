using Acsp.Core;

namespace Acsp.Solver;

/// <summary>
/// Chains the selected flight strings into per-fleet rotations (RP-1-CYCLE) by FIFO-matching
/// string arrivals to string departures at each airport. The number of aircraft used is
/// determined by the arrival/departure pattern, not by the particular matching, so FIFO
/// reproduces the fleet usage of the LP's ground arcs.
/// </summary>
public static class SolutionAssembler
{
    public static void AssembleRotations(Instance inst, Solution sol)
    {
        sol.Rotations.Clear();
        var p = inst.Period;
        foreach (var fleetGroup in sol.SelectedStrings.GroupBy(s => s.FleetId))
        {
            int k = fleetGroup.Key;
            var strings = fleetGroup.ToList();
            int trailing(FlightString s) => sol.WithMaintenance
                ? inst.Fleets[k].MaintenanceDuration
                : inst.MinGroundTime(inst.FlightDestination(inst.Flights[s.FlightIds[^1]]), k);

            // successor[i] = index of the string that the aircraft flies after strings[i]
            var successor = new int[strings.Count];
            Array.Fill(successor, -1);

            foreach (var airportGroup in Enumerable.Range(0, strings.Count)
                         .GroupBy(i => inst.FlightDestination(inst.Flights[strings[i].FlightIds[^1]])))
            {
                int airport = airportGroup.Key;
                var arrivals = airportGroup
                    .Select(i => (Idx: i, Ready: p.Wrap(
                        inst.FlightArr(inst.Flights[strings[i].FlightIds[^1]]) + trailing(strings[i]))))
                    .OrderBy(x => x.Ready).ToList();
                var departures = Enumerable.Range(0, strings.Count)
                    .Where(j => inst.FlightOrigin(inst.Flights[strings[j].FlightIds[0]]) == airport)
                    .Select(j => (Idx: j, Dep: inst.FlightDep(inst.Flights[strings[j].FlightIds[0]])))
                    .OrderBy(x => x.Dep).ToList();
                if (arrivals.Count != departures.Count)
                    throw new InvalidOperationException(
                        $"unbalanced strings at airport {inst.Airports[airport].Code} for fleet {k}: " +
                        $"{arrivals.Count} arrivals vs {departures.Count} departures");

                // FIFO within the week, remaining departures wrap to leftover arrivals
                var queue = new Queue<int>();
                var unmatched = new Queue<int>();
                int ai = 0;
                foreach (var (j, dep) in departures)
                {
                    while (ai < arrivals.Count && arrivals[ai].Ready <= dep) queue.Enqueue(arrivals[ai++].Idx);
                    if (queue.Count > 0) successor[queue.Dequeue()] = j;
                    else unmatched.Enqueue(j);
                }
                while (ai < arrivals.Count) queue.Enqueue(arrivals[ai++].Idx);
                while (unmatched.Count > 0) successor[queue.Dequeue()] = unmatched.Dequeue();
                if (queue.Count > 0)
                    throw new InvalidOperationException("FIFO matching left arrivals unmatched");
            }

            // follow successors to build cycles
            var visited = new bool[strings.Count];
            for (int start = 0; start < strings.Count; start++)
            {
                if (visited[start]) continue;
                var cycle = new List<FlightString>();
                int cur = start;
                while (!visited[cur])
                {
                    visited[cur] = true;
                    cycle.Add(strings[cur]);
                    cur = successor[cur];
                    if (cur < 0) throw new InvalidOperationException("broken rotation chain");
                }
                sol.Rotations.Add(new Rotation { FleetId = k, Strings = cycle });
            }
        }
    }
}
