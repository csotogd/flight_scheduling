namespace Acsp.Core;

/// <summary>
/// A flight/route: a sequence of connected legs (§3.1.5). Cargo flights are conducted by the
/// airline's own fleet (mandatory or optional); external flights (PAX bellies, RFS, partners)
/// only offer capacity for cargo routing.
/// </summary>
public sealed class Flight
{
    public required int Id { get; init; }
    public required string Code { get; init; }
    /// <summary>Leg ids in sequence order.</summary>
    public required int[] LegIds { get; init; }
    public required bool IsExternal { get; init; }
    /// <summary>mdt_f: must be part of the final schedule. Only meaningful for cargo flights.</summary>
    public bool IsMandatory { get; init; }

    /// <summary>cflt_{k,f}: fixed cost per fleet type (cargo flights). Indexed by fleet id.</summary>
    public double[] FixedCostByFleet { get; init; } = [];

    /// <summary>cflt_f: fixed cost of booking this external flight (extension §4.1). 0 = always available.</summary>
    public double ExternalFixedCost { get; init; }

    /// <summary>Optional explicit fleet exclusions on top of computed compatibility. Indexed by fleet id.</summary>
    public bool[]? ForbiddenFleets { get; init; }

    public bool IsOptionalCargo => !IsExternal && !IsMandatory;
    public int NumLegs => LegIds.Length;
}
