namespace Acsp.Core;

/// <summary>A fleet type (§3.1.3), e.g. B747F.</summary>
public sealed class FleetType
{
    public required int Id { get; init; }
    public required string Code { get; init; }

    /// <summary>n_k: number of available airplanes of this type.</summary>
    public required int Count { get; init; }

    /// <summary>cacr_k: fixed cost per aircraft over the planning period (leasing, capital, ...).</summary>
    public required double FixedCostPerAircraft { get; init; }

    /// <summary>wmax_k: maximal payload weight in tonnes.</summary>
    public required double MaxWeight { get; init; }

    /// <summary>vmax_k: maximal payload volume in m3.</summary>
    public required double MaxVolume { get; init; }

    /// <summary>Range in km at FULL payload (the payload-range breakpoint). Also the
    /// fleet-flight compatibility limit when no curve is configured (RangeMaxKm = 0).</summary>
    public required double RangeKm { get; init; }

    /// <summary>Maximum range in km, reached at PayloadAtMaxRangeT (fuel-limited point of the
    /// payload-range curve); beyond it the leg is unflyable even empty. 0 = no curve: the
    /// classic single-range model (RangeKm limits compatibility, payload never derated).</summary>
    public double RangeMaxKm { get; init; }

    /// <summary>Payload in tonnes at RangeMaxKm. 0 = half of MaxWeight (typical fuel-volume
    /// tradeoff point). Only meaningful when RangeMaxKm &gt; 0.</summary>
    public double PayloadAtMaxRangeT { get; init; }

    /// <summary>
    /// The payload-range frontier, inverted: maximal payload (tonnes) this fleet can carry on
    /// a leg of the given distance. Flat at MaxWeight up to RangeKm, linear down to
    /// PayloadAtMaxRangeT at RangeMaxKm, 0 (incompatible) beyond. Enters the model as the
    /// per-(leg, fleet) capacity coefficient — exact and linear, no optimization cost.
    /// </summary>
    public double PayloadAtKm(double distanceKm)
    {
        if (distanceKm <= RangeKm) return MaxWeight;
        double rangeMax = RangeMaxKm > 0 ? RangeMaxKm : RangeKm;
        if (distanceKm > rangeMax) return 0;
        double floor = PayloadAtMaxRangeT > 0 ? PayloadAtMaxRangeT : MaxWeight / 2;
        return MaxWeight - (MaxWeight - floor) * (distanceKm - RangeKm) / (rangeMax - RangeKm);
    }

    public double CruiseSpeedKmH { get; init; } = 850;

    /// <summary>Default gtmin_{a,k} in minutes when the airport has no override.</summary>
    public int DefaultMinGroundTime { get; init; } = 60;

    // Maintenance (A-check) regulations (RP-4-MNT). int.MaxValue = unconstrained.
    /// <summary>mntcycles_k: maximal number of cycles (take-offs/landings) between checks.</summary>
    public int MaxCyclesBetweenMaintenance { get; init; } = int.MaxValue;
    /// <summary>mntflight_k: maximal flight minutes between checks.</summary>
    public int MaxFlightMinutesBetweenMaintenance { get; init; } = int.MaxValue;
    /// <summary>mntelapsed_k: maximal elapsed minutes between checks.</summary>
    public int MaxElapsedMinutesBetweenMaintenance { get; init; } = int.MaxValue;
    /// <summary>Minimal duration in minutes of a maintenance stop (appended to augmented flight strings).</summary>
    public int MaintenanceDuration { get; init; } = 8 * 60;
}
