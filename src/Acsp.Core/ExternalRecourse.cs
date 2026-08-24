namespace Acsp.Core;

/// <summary>
/// Deliver-all recourse pricing: when an instance carries a service commitment
/// (<see cref="Instance.DeliverAll"/>), every O&amp;D can always be delivered by a
/// contracted external carrier at a multiple of the airline's own flying economics —
/// so serving everything is feasible by construction and the optimization becomes
/// "own network vs contracting bill". The per-tonne cost is derived from the instance
/// itself (average own variable + amortized fixed cost per km, times the multiplier),
/// keeping the recourse consistently priced across instances.
/// </summary>
public static class ExternalRecourse
{
    public const double DefaultMultiplier = 3.0;

    /// <summary>Contracted delivery cost per tonne for every od (great-circle based).</summary>
    public static double[] CostPerTonne(Instance inst, double multiplier = DefaultMultiplier)
    {
        // own economics per km, from the instance (same shape as the flight proposer uses)
        double varPerKm = inst.Legs.Length > 0
            ? inst.Legs.Average(l => l.VariableCostPerTonne / Math.Max(1, l.DistanceKm))
            : 0.035;
        double fixedPerKm = 4.0;
        var ratios = inst.CargoFlights
            .SelectMany(f => Enumerable.Range(0, inst.Fleets.Length)
                .Where(k => f.FixedCostByFleet.Length > k)
                .Select(k => f.FixedCostByFleet[k]
                    / Math.Max(1, f.LegIds.Sum(l => inst.Legs[l].DistanceKm))))
            .OrderBy(x => x).ToList();
        if (ratios.Count > 0) fixedPerKm = ratios[ratios.Count / 2];
        double capT = inst.Fleets.Length > 0 ? inst.Fleets.Max(k => k.MaxWeight) : 100;

        var cost = new double[inst.Ods.Length];
        foreach (var od in inst.Ods)
        {
            double dist = HaversineKm(inst.Airports[od.Origin], inst.Airports[od.Destination]);
            cost[od.Id] = Math.Round(
                multiplier * (dist * varPerKm + dist * fixedPerKm / capT), 2);
        }
        return cost;
    }

    private static double HaversineKm(Airport a, Airport b)
    {
        double dLat = (b.Lat - a.Lat) * Math.PI / 180, dLon = (b.Lon - a.Lon) * Math.PI / 180;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(a.Lat * Math.PI / 180) * Math.Cos(b.Lat * Math.PI / 180) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 6371.0 * 2 * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}
