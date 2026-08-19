namespace Acsp.Core;

/// <summary>An O&amp;D pair: a transportation demand from an origin to a destination (§3.1.7).</summary>
public sealed class Od
{
    public required int Id { get; init; }
    public required int Origin { get; init; }
    public required int Destination { get; init; }
    /// <summary>avail_od in [0, N): earliest pickup time at the origin.</summary>
    public required int Avail { get; init; }
    /// <summary>dtmax_od: maximal delivery duration in minutes (measured from avail).</summary>
    public required int MaxDeliveryTime { get; init; }
    /// <summary>w_od: demand weight in tonnes (arbitrarily divisible).</summary>
    public required double Weight { get; init; }
    /// <summary>v_od: demand volume in m3.</summary>
    public required double Volume { get; init; }
    /// <summary>r_od: freight rate in US$ per tonne.</summary>
    public required double Rate { get; init; }

    /// <summary>vol_od: volume per tonne (m3/t), the coefficient of flow in volume constraints.</summary>
    public double VolumePerTonne => Weight > 0 ? Volume / Weight : 0;
}
