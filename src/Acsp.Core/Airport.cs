namespace Acsp.Core;

/// <summary>An airport/destination (§3.1.2). Ids are dense indices into Instance.Airports.</summary>
public sealed class Airport
{
    public required int Id { get; init; }
    public required string Code { get; init; }
    public string Name { get; init; } = "";
    public double Lat { get; init; }
    public double Lon { get; init; }

    /// <summary>hub_a: cargo may be transferred between flights at this airport.</summary>
    public bool IsTransferHub { get; init; }

    /// <summary>Minimal time (min) to transfer cargo between two flights here (CR-7-XFRTIME).</summary>
    public int MinTransferTime { get; init; }

    /// <summary>Cost in US$ per tonne transferred between flights at this airport.</summary>
    public double TransferCostPerTonne { get; init; }

    /// <summary>Cost in US$ per tonne per hour stored at this airport.</summary>
    public double StorageCostPerTonneHour { get; init; }

    /// <summary>hubmnt_{k,a}: maintenance (A-check) can be performed here for fleet k. Indexed by fleet id.</summary>
    public bool[] MaintenanceHubFor { get; init; } = [];

    /// <summary>cmnt_{k,a}: cost of a maintenance stop for fleet k at this airport. Indexed by fleet id.</summary>
    public double[] MaintenanceCost { get; init; } = [];

    /// <summary>gtmin_{a,k} override in minutes; -1 = use fleet default. Indexed by fleet id.</summary>
    public int[] MinGroundTimeOverride { get; init; } = [];
}
