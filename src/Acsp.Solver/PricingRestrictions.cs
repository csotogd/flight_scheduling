using Acsp.Core;

namespace Acsp.Solver;

/// <summary>
/// Visibility flags restricting the pricing graphs according to the branching decisions on the
/// current branch-and-bound node (§5: attribute visible_v / visible_e, §7).
/// </summary>
public sealed class PricingRestrictions
{
    /// <summary>Legs usable by cargo flow paths (false when the owning flight is branched out).</summary>
    public required bool[] LegVisible { get; init; }
    /// <summary>[fleet][flight]: flight may be covered by a string of this fleet.</summary>
    public required bool[][] FlightVisibleForFleet { get; init; }
    /// <summary>Follow-on 0-branches: strings may not cover j immediately after i.</summary>
    public HashSet<(int I, int J)> ForbiddenFollowOns { get; } = [];
    /// <summary>Follow-on 1-branches: a string covering i must cover exactly j immediately after.</summary>
    public Dictionary<int, int> ForcedFollowOns { get; } = [];

    public static PricingRestrictions AllowAll(Instance inst)
    {
        var legVisible = new bool[inst.Legs.Length];
        Array.Fill(legVisible, true);
        var byFleet = new bool[inst.Fleets.Length][];
        for (int k = 0; k < inst.Fleets.Length; k++)
        {
            byFleet[k] = new bool[inst.Flights.Length];
            foreach (var f in inst.CargoFlights)
                byFleet[k][f.Id] = inst.Compatible(k, f.Id);
        }
        return new PricingRestrictions { LegVisible = legVisible, FlightVisibleForFleet = byFleet };
    }

    public PricingRestrictions Clone()
    {
        var c = new PricingRestrictions
        {
            LegVisible = (bool[])LegVisible.Clone(),
            FlightVisibleForFleet = FlightVisibleForFleet.Select(a => (bool[])a.Clone()).ToArray(),
        };
        foreach (var x in ForbiddenFollowOns) c.ForbiddenFollowOns.Add(x);
        foreach (var (i, j) in ForcedFollowOns) c.ForcedFollowOns[i] = j;
        return c;
    }

    /// <summary>Removes a cargo flight entirely (optional-flight 0-branch).</summary>
    public void ExcludeFlight(Instance inst, int flightId)
    {
        foreach (var arr in FlightVisibleForFleet) arr[flightId] = false;
        foreach (var lid in inst.Flights[flightId].LegIds) LegVisible[lid] = false;
    }

    /// <summary>Hides the legs of an external flight (external 0-branch).</summary>
    public void ExcludeExternalFlight(Instance inst, int flightId)
    {
        foreach (var lid in inst.Flights[flightId].LegIds) LegVisible[lid] = false;
    }

    /// <summary>Fleet-flight branch: 1-branch forces flight to fleet k, 0-branch forbids it.</summary>
    public void RestrictFleet(int flightId, int fleetId, bool force)
    {
        for (int k = 0; k < FlightVisibleForFleet.Length; k++)
            if (force ? k != fleetId : k == fleetId)
                FlightVisibleForFleet[k][flightId] = false;
    }

    /// <summary>True if a string arc from flight i directly to flight j is allowed.</summary>
    public bool FollowOnAllowed(int i, int j)
    {
        if (ForbiddenFollowOns.Contains((i, j))) return false;
        if (ForcedFollowOns.TryGetValue(i, out var forced) && forced != j) return false;
        return true;
    }

    /// <summary>True if a string may end (go to the sink) after flight i.</summary>
    public bool MayEndAfter(int i) => !ForcedFollowOns.ContainsKey(i);

    /// <summary>Checks an existing string column against the restrictions (for RMP filtering).</summary>
    public bool Allows(FlightString s)
    {
        for (int i = 0; i < s.FlightIds.Length; i++)
        {
            if (!FlightVisibleForFleet[s.FleetId][s.FlightIds[i]]) return false;
            if (i + 1 < s.FlightIds.Length && !FollowOnAllowed(s.FlightIds[i], s.FlightIds[i + 1])) return false;
        }
        return MayEndAfter(s.FlightIds[^1]);
    }

    /// <summary>Checks an existing path column against the restrictions.</summary>
    public bool Allows(CargoPath p) => p.LegIds.All(l => LegVisible[l]);
}
