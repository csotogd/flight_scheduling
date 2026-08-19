namespace Acsp.Core;

/// <summary>A flight leg: a direct connection between two airports (§3.1.4).</summary>
public sealed class Leg
{
    public required int Id { get; init; }
    /// <summary>Flight this leg belongs to (f_l). Set when flights are built.</summary>
    public required int FlightId { get; init; }
    public required int Origin { get; init; }
    public required int Destination { get; init; }
    /// <summary>dep_l in [0, N): minute the airplane leaves its parking position.</summary>
    public required int Dep { get; init; }
    /// <summary>arr_l in [0, N).</summary>
    public required int Arr { get; init; }
    public required double DistanceKm { get; init; }
    /// <summary>cleg_l: variable transportation cost in US$ per tonne (fleet-independent, §2.2.2).</summary>
    public required double VariableCostPerTonne { get; init; }
    /// <summary>Taxi minutes subtracted from block time to obtain flight time.</summary>
    public int TaxiMinutes { get; init; } = 20;

    /// <summary>Maximal payload weight (t) for external legs; ignored for cargo legs (fleet-dependent).</summary>
    public double MaxWeight { get; init; }
    /// <summary>Maximal payload volume (m3) for external legs; ignored for cargo legs.</summary>
    public double MaxVolume { get; init; }

    /// <summary>dur_l: block time in minutes.</summary>
    public int BlockTime(Period p) => p.Time(Dep, Arr);
    /// <summary>ft_l: flight time = block time minus taxi times.</summary>
    public int FlightTime(Period p) => Math.Max(0, BlockTime(p) - TaxiMinutes);
}
