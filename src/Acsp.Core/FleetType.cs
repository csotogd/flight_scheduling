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

    /// <summary>Maximal leg range in km (used for fleet-flight compatibility, FA-3-COMP).</summary>
    public required double RangeKm { get; init; }

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
