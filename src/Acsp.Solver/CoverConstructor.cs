using Acsp.Core;

namespace Acsp.Solver;

/// <summary>
/// Constructive feasibility for the no-maintenance model (FARP-T), where strings are single
/// flights and rotations chain them through the timeline: covering the mandatory flights is
/// a balanced fleet-ASSIGNMENT problem. Each mandatory flight gets a compatible fleet such
/// that per (fleet, hub) weekly arrivals equal departures — round trips (origin hub =
/// destination hub) balance themselves, inter-hub flights are assigned in mirror pairs —
/// and hours are spread across fleets. The result is a real schedule (no cargo routed,
/// negative profit) used as guaranteed initial incumbent; if it verifies, it is a
/// constructive proof of feasibility.
/// </summary>
public static class CoverConstructor
{
    public sealed record UncoveredFlight(string Code, string Reason);
    public sealed record Result(Solution? Solution, List<UncoveredFlight> Uncovered,
        int[] FlightsPerFleet, double[] HoursPerFleet);

    public static Result Build(Instance inst)
    {
        var failures = new List<UncoveredFlight>();
        var hoursUsed = new double[inst.Fleets.Length];
        var flightsPerFleet = new int[inst.Fleets.Length];
        var strings = new List<FlightString>();

        double Hours(Flight f) => inst.FlightDuration(f) / 60.0;
        var hoursBudget = inst.Fleets.Select(k => Math.Max(1.0, k.Count * (inst.Period.N / 60.0)))
            .ToArray();

        void Assign(int fleet, params Flight[] flights)
        {
            foreach (var f in flights)
            {
                strings.Add(new FlightString { FleetId = fleet, FlightIds = [f.Id] });
                flightsPerFleet[fleet]++;
                hoursUsed[fleet] += Hours(f);
            }
        }

        int? LightestCompatible(params Flight[] flights) => Enumerable
            .Range(0, inst.Fleets.Length)
            .Where(k => flights.All(f => inst.Compatible(k, f.Id)))
            .OrderBy(k => hoursUsed[k] / hoursBudget[k])
            .Select(k => (int?)k).FirstOrDefault();

        var mirror = new Dictionary<int, int>(); // inter-hub flight -> its assigned pair
        var mandatory = inst.MandatoryFlights.ToList();
        var roundTrips = mandatory
            .Where(f => inst.FlightOrigin(f) == inst.FlightDestination(f)).ToList();
        var interHub = mandatory
            .Where(f => inst.FlightOrigin(f) != inst.FlightDestination(f)).ToList();

        // inter-hub flights must balance arrivals and departures per (fleet, airport):
        // pair each A->B with a B->A of the same endpoints and assign the pair together
        var byCorridor = interHub.GroupBy(f =>
        {
            int a = inst.FlightOrigin(f), b = inst.FlightDestination(f);
            return (Math.Min(a, b), Math.Max(a, b));
        });
        foreach (var corridor in byCorridor)
        {
            var fwd = corridor.Where(f => inst.FlightOrigin(f) == corridor.Key.Item1)
                .OrderBy(f => inst.FlightDep(f)).ToList();
            var back = corridor.Where(f => inst.FlightOrigin(f) == corridor.Key.Item2)
                .OrderBy(f => inst.FlightDep(f)).ToList();
            int pairs = Math.Min(fwd.Count, back.Count);
            for (int i = 0; i < pairs; i++)
            {
                if (LightestCompatible(fwd[i], back[i]) is int k)
                {
                    Assign(k, fwd[i], back[i]);
                    mirror[fwd[i].Id] = back[i].Id;
                    mirror[back[i].Id] = fwd[i].Id;
                }
                else
                {
                    failures.Add(new(fwd[i].Code, "no fleet compatible with mirror pair"));
                    failures.Add(new(back[i].Code, "no fleet compatible with mirror pair"));
                }
            }
            // unmatched directions cannot be balanced by any assignment: report them
            foreach (var f in fwd.Skip(pairs).Concat(back.Skip(pairs)))
                failures.Add(new(f.Code,
                    $"unbalanced inter-hub corridor " +
                    $"{inst.Airports[corridor.Key.Item1].Code}-{inst.Airports[corridor.Key.Item2].Code} " +
                    $"({fwd.Count} outbound vs {back.Count} return)"));
        }

        // round trips balance themselves at their hub: spread by relative fleet load
        foreach (var f in roundTrips.OrderByDescending(Hours))
        {
            if (LightestCompatible(f) is int k) Assign(k, f);
            else failures.Add(new(f.Code, "no compatible fleet"));
        }

        if (failures.Count > 0)
            return new Result(null, failures, flightsPerFleet, hoursUsed);

        // repair loop: the assembler decides how many aircraft the assignment really needs;
        // while some fleet exceeds its count, move self-balanced round trips out of that
        // fleet's smallest rotations into compatible fleets with slack and reassemble
        Solution sol = Assemble();
        for (int iter = 0; iter < 60; iter++)
        {
            var needed = AircraftNeeded(sol);
            var over = Enumerable.Range(0, inst.Fleets.Length)
                .Where(k => needed[k] > inst.Fleets[k].Count)
                .OrderByDescending(k => needed[k] - inst.Fleets[k].Count)
                .Select(k => (int?)k).FirstOrDefault();
            if (over is not int ok) break;

            // candidate moves: round trips out of the overloaded fleet's smallest rotations
            // into any compatible fleet. A fleet at its aircraft limit can often still absorb
            // a flight for free (packed into existing rotations' idle time), so moves are
            // EVALUATED by reassembling rather than filtered by a-priori slack.
            int Violation(int[] n) => Enumerable.Range(0, inst.Fleets.Length)
                .Sum(k => Math.Max(0, n[k] - inst.Fleets[k].Count));
            int current = Violation(needed);
            // a move is one round trip alone, or an inter-hub MIRROR PAIR moved together
            // (balance per (fleet, hub) must survive every move)
            var candidates = sol.Rotations.Where(r => r.FleetId == ok)
                .OrderBy(r => r.Strings.Count)
                .SelectMany(r => r.Strings)
                .Select(s => inst.Flights[s.FlightIds[0]])
                .Select(f => mirror.TryGetValue(f.Id, out int m) && m < f.Id
                    ? null // pair enumerated once, via its lower id
                    : (int[]?)(mirror.TryGetValue(f.Id, out int mm) ? [f.Id, mm] : [f.Id]))
                .Where(g => g is not null)
                .SelectMany(g => Enumerable.Range(0, inst.Fleets.Length)
                    .Where(k => k != ok && g!.All(fid => inst.Compatible(k, fid)))
                    .OrderBy(k => (double)needed[k] / inst.Fleets[k].Count)
                    .Select(k => (Group: g!, Target: k)))
                .Take(60);
            var moved = false;
            foreach (var (group, t) in candidates)
            {
                var olds = group
                    .Select(fid => strings.First(x => x.FleetId == ok && x.FlightIds[0] == fid))
                    .ToList();
                foreach (var s in olds) strings.Remove(s);
                foreach (var s in olds)
                    strings.Add(new FlightString { FleetId = t, FlightIds = s.FlightIds });
                var trial = Assemble();
                if (Violation(AircraftNeeded(trial)) < current)
                {
                    foreach (var fid in group)
                    {
                        var f = inst.Flights[fid];
                        flightsPerFleet[ok]--; flightsPerFleet[t]++;
                        hoursUsed[ok] -= Hours(f); hoursUsed[t] += Hours(f);
                    }
                    sol = trial;
                    moved = true;
                    break;
                }
                // revert
                strings.RemoveRange(strings.Count - olds.Count, olds.Count);
                strings.AddRange(olds);
            }
            if (!moved) break; // no improving move found: give up, report as-is
        }
        return new Result(sol, failures, flightsPerFleet, hoursUsed);

        Solution Assemble()
        {
            var s = new Solution
            {
                SelectedStrings = [.. strings],
                Flows = [],
                SelectedExternalFlights = [],
                WithMaintenance = false,
            };
            SolutionAssembler.AssembleRotations(inst, s);
            return s;
        }

        int[] AircraftNeeded(Solution s)
        {
            var needed = new int[inst.Fleets.Length];
            foreach (var r in s.Rotations) needed[r.FleetId] += r.AircraftNeeded(inst);
            return needed;
        }
    }
}
