using Acsp.Core;
using Acsp.Solver.Lp;

namespace Acsp.Solver;

/// <summary>
/// The baseline of §9.3 (first block of Table 3): solve ACSP-T directly as a MIP with all cargo
/// flow paths pre-generated. Only practical for small instances. Without maintenance the flight
/// strings are exactly the compatible (fleet, flight) pairs; with maintenance the complete
/// string set must be supplied by the caller (only viable for tiny instances).
/// </summary>
public sealed class DirectMipSolver
{
    public sealed record DirectResult(LpStatus Status, double Objective, Solution? Solution, int Paths, int Strings);

    public static DirectResult Solve(Instance inst, bool withMaintenance,
        IEnumerable<CargoPath> allPaths, IEnumerable<FlightString>? allStrings = null,
        double timeLimitSeconds = 3600, double gap = 1e-6, string backend = "highs")
    {
        using var rmp = new Rmp(inst, withMaintenance, LpSolverFactory.Create(backend));
        int nPaths = 0, nStrings = 0;
        foreach (var p in allPaths)
            if (rmp.AddPath(p)) nPaths++;
        if (allStrings is null)
        {
            if (withMaintenance)
                throw new ArgumentException("with maintenance the caller must supply all strings");
            foreach (var f in inst.CargoFlights)
                for (int k = 0; k < inst.Fleets.Length; k++)
                    if (inst.Compatible(k, f.Id) &&
                        rmp.AddString(new FlightString { FleetId = k, FlightIds = [f.Id] }))
                        nStrings++;
        }
        else
        {
            foreach (var s in allStrings)
                if (rmp.AddString(s)) nStrings++;
        }
        var res = rmp.SolveMipOnCurrentColumns(timeLimitSeconds, gap);
        Solution? sol = null;
        if (res.Status is LpStatus.Optimal or LpStatus.TimeLimit && rmp.IsIntegral(res))
        {
            sol = rmp.ExtractSolution(res);
            SolutionAssembler.AssembleRotations(inst, sol);
        }
        return new DirectResult(res.Status, res.Objective, sol, nPaths, nStrings);
    }

    /// <summary>Enumerates every feasible cargo flow path with a bounded number of legs.</summary>
    public static IEnumerable<CargoPath> EnumeratePaths(Instance inst, int maxLegs = 5)
    {
        var pricer = new PathPricerEnumerator(inst, maxLegs);
        return pricer.All();
    }

    /// <summary>DFS path enumeration honoring the same feasibility rules as CargoPath.IsFeasible.</summary>
    private sealed class PathPricerEnumerator(Instance inst, int maxLegs)
    {
        private readonly List<CargoPath> _result = [];

        public List<CargoPath> All()
        {
            var legsByOrigin = inst.Legs.GroupBy(l => l.Origin).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var od in inst.Ods)
            {
                foreach (var first in legsByOrigin.GetValueOrDefault(od.Origin, []))
                    Dfs(od, [first.Id], legsByOrigin);
            }
            return _result;
        }

        private void Dfs(Od od, List<int> stack, Dictionary<int, List<Leg>> legsByOrigin)
        {
            var probe = new CargoPath { OdId = od.Id, LegIds = [.. stack] };
            if (probe.TotalDeliveryTime(inst) > od.MaxDeliveryTime) return;
            var last = inst.Legs[stack[^1]];
            if (last.Destination == od.Destination)
            {
                if (probe.IsFeasible(inst, out _)) _result.Add(probe);
                return;
            }
            if (stack.Count >= maxLegs) return;
            foreach (var next in legsByOrigin.GetValueOrDefault(last.Destination, []))
            {
                if (stack.Contains(next.Id)) continue;
                // quick structural filter; full check happens at completion
                if (next.FlightId != last.FlightId && !inst.Airports[last.Destination].IsTransferHub)
                    continue;
                stack.Add(next.Id);
                Dfs(od, stack, legsByOrigin);
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }
}
