using Acsp.Core;
using Acsp.Solver.Lp;

namespace Acsp.Solver;

/// <summary>The branching state of one branch-and-bound node.</summary>
public sealed class BranchState
{
    public required PricingRestrictions Restrictions { get; init; }
    public Dictionary<int, bool> ForcedFlights { get; init; } = [];
    public Dictionary<int, bool> ForcedExternals { get; init; } = [];
    public Dictionary<string, bool> FixedStrings { get; init; } = [];
    public double InheritedBound { get; set; } = double.PositiveInfinity;
    public int Depth { get; init; }

    public BranchState Clone(double bound) => new()
    {
        Restrictions = Restrictions.Clone(),
        ForcedFlights = new(ForcedFlights),
        ForcedExternals = new(ForcedExternals),
        FixedStrings = new(FixedStrings),
        InheritedBound = bound,
        Depth = Depth + 1,
    };

    public static BranchState Root(Instance inst) => new()
    {
        Restrictions = PricingRestrictions.AllowAll(inst),
    };
}

/// <summary>
/// The problem-specific branching strategies of §7, tried in order: optional flight branching,
/// fleet-flight branching, follow-on branching, classical branching on external flights, and a
/// fallback on individual flight strings.
/// </summary>
public static class Branching
{
    public sealed record Decision(string Kind, string Description, BranchState OneBranch, BranchState ZeroBranch);

    public static Decision? Decide(Instance inst, Rmp rmp, LpResult lp, BranchState state,
        double tol = 1e-6)
    {
        // aggregate string values
        var coverByFlight = new double[inst.Flights.Length];
        var coverByFleetFlight = new Dictionary<(int K, int F), double>();
        var followOn = new Dictionary<(int I, int J), double>();
        foreach (var sc in rmp.Strings)
        {
            double y = lp.ColumnValues[sc.Col];
            if (y <= tol) continue;
            foreach (var fid in sc.Str.FlightIds)
            {
                coverByFlight[fid] += y;
                var key = (sc.Str.FleetId, fid);
                coverByFleetFlight[key] = coverByFleetFlight.GetValueOrDefault(key) + y;
            }
            for (int i = 0; i + 1 < sc.Str.FlightIds.Length; i++)
            {
                var key = (sc.Str.FlightIds[i], sc.Str.FlightIds[i + 1]);
                followOn[key] = followOn.GetValueOrDefault(key) + y;
            }
        }

        static bool Frac(double v, double tol) => v > tol && v < 1 - tol;

        // ---- 1. optional flight branching (score per §7)
        var fracOptional = inst.OptionalFlights
            .Where(f => !state.ForcedFlights.ContainsKey(f.Id) && Frac(coverByFlight[f.Id], tol))
            .ToList();
        if (fracOptional.Count > 0)
        {
            var best = fracOptional.OrderByDescending(f => Score(inst, rmp, lp, f)).First();
            var one = state.Clone(lp.Objective);
            one.ForcedFlights[best.Id] = true;
            var zero = state.Clone(lp.Objective);
            zero.ForcedFlights[best.Id] = false;
            zero.Restrictions.ExcludeFlight(inst, best.Id);
            return new Decision("optional-flight", $"flight {best.Code}", one, zero);
        }

        // ---- 2. fleet-flight branching
        var fracFleetFlight = coverByFleetFlight.Where(kv => Frac(kv.Value, tol)).ToList();
        if (fracFleetFlight.Count > 0)
        {
            var ((k, f), _) = fracFleetFlight.OrderByDescending(kv => kv.Value).First();
            var one = state.Clone(lp.Objective);
            one.Restrictions.RestrictFleet(f, k, force: true);
            var zero = state.Clone(lp.Objective);
            zero.Restrictions.RestrictFleet(f, k, force: false);
            return new Decision("fleet-flight",
                $"flight {inst.Flights[f].Code} by fleet {inst.Fleets[k].Code}", one, zero);
        }

        // ---- 3. follow-on branching (Ryan-Foster variant)
        var fracFollowOn = followOn.Where(kv => Frac(kv.Value, tol)).ToList();
        if (fracFollowOn.Count > 0)
        {
            var ((i, j), _) = fracFollowOn.OrderByDescending(kv => kv.Value).First();
            var one = state.Clone(lp.Objective);
            one.Restrictions.ForcedFollowOns[i] = j;
            var zero = state.Clone(lp.Objective);
            zero.Restrictions.ForbiddenFollowOns.Add((i, j));
            return new Decision("follow-on",
                $"{inst.Flights[i].Code} -> {inst.Flights[j].Code}", one, zero);
        }

        // ---- 4. classical branching on external flight selectors (§7, last paragraph)
        foreach (var (fid, col) in rmp.ExternalColumns)
        {
            if (state.ForcedExternals.ContainsKey(fid) || !Frac(lp.ColumnValues[col], tol)) continue;
            var one = state.Clone(lp.Objective);
            one.ForcedExternals[fid] = true;
            var zero = state.Clone(lp.Objective);
            zero.ForcedExternals[fid] = false;
            zero.Restrictions.ExcludeExternalFlight(inst, fid);
            return new Decision("external", $"external flight {inst.Flights[fid].Code}", one, zero);
        }

        // ---- 5. fallback: branch on the most fractional flight string variable
        Rmp.StringCol? fracString = null;
        double bestDist = 0.5 - tol;
        foreach (var sc in rmp.Strings)
        {
            double y = lp.ColumnValues[sc.Col];
            if (!Frac(y, tol)) continue;
            double dist = Math.Abs(y - 0.5);
            if (fracString is null || dist < bestDist) { fracString = sc; bestDist = dist; }
        }
        if (fracString is not null)
        {
            var one = state.Clone(lp.Objective);
            one.FixedStrings[fracString.Str.Key()] = true;
            var zero = state.Clone(lp.Objective);
            zero.FixedStrings[fracString.Str.Key()] = false;
            return new Decision("string", $"string [{fracString.Str.Key()}]", one, zero);
        }

        return null; // integral
    }

    /// <summary>score(f) of §7: attributed path margins on the legs of f minus minimal fixed costs.</summary>
    private static double Score(Instance inst, Rmp rmp, LpResult lp, Flight f)
    {
        double margin = 0;
        var legSet = f.LegIds.ToHashSet();
        double[] legLoad = new double[inst.Legs.Length];
        foreach (var pc in rmp.Paths)
        {
            double x = lp.ColumnValues[pc.Col];
            if (x <= 1e-9) continue;
            double pathDist = pc.Path.LegIds.Sum(l => inst.Legs[l].DistanceKm);
            if (pathDist <= 0) continue;
            double m = pc.Path.Margin(inst);
            foreach (var lid in pc.Path.LegIds)
            {
                if (legSet.Contains(lid))
                    margin += m * x * (inst.Legs[lid].DistanceKm / pathDist);
                legLoad[lid] += x;
            }
        }
        double load = f.LegIds.Max(l => legLoad[l]);
        double minFixed = double.PositiveInfinity;
        for (int k = 0; k < inst.Fleets.Length; k++)
            if (inst.Compatible(k, f.Id) && inst.Fleets[k].MaxWeight >= load - 1e-9)
                minFixed = Math.Min(minFixed, f.FixedCostByFleet[k]);
        if (double.IsInfinity(minFixed))
            minFixed = f.FixedCostByFleet.Where((_, k) => inst.Compatible(k, f.Id))
                .DefaultIfEmpty(0).Min();
        return margin - minFixed;
    }
}
