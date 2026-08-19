namespace Acsp.Solver;

/// <summary>
/// Dual values of the restricted master problem, with the sign convention of a maximization
/// LP with rows in &lt;= or = form (duals of &lt;= rows are >= 0, duals of = rows are free):
///   reduced cost of a column = objective coefficient - sum_r a_{r,col} * pi_r.
/// </summary>
public sealed class MasterDuals
{
    /// <summary>pi_od of demand rows (14), indexed by od id.</summary>
    public required double[] OdDemand { get; init; }
    /// <summary>pi^w_l of weight rows (15)/(17), indexed by leg id.</summary>
    public required double[] LegWeight { get; init; }
    /// <summary>pi^v_l of volume rows (16)/(18), indexed by leg id.</summary>
    public required double[] LegVolume { get; init; }
    /// <summary>pi_f of flight cover rows (19), indexed by flight id (0 for external flights).</summary>
    public required double[] FlightCover { get; init; }
    /// <summary>pi^dep_{k,f} of departure flow balance rows (20), [fleet, flight].</summary>
    public required double[,] DepBalance { get; init; }
    /// <summary>pi^arr_{k,f} of arrival flow balance rows (21), [fleet, flight].</summary>
    public required double[,] ArrBalance { get; init; }
    /// <summary>pi_k of fleet size rows (22), indexed by fleet id.</summary>
    public required double[] FleetSize { get; init; }
    /// <summary>pi^ibc_{od,f} of implied bound cuts (35), keyed by (od id, optional flight id).</summary>
    public Dictionary<(int Od, int Flight), double> ImpliedBoundCuts { get; init; } = [];

    public static MasterDuals Zero(Acsp.Core.Instance inst) => new()
    {
        OdDemand = new double[inst.Ods.Length],
        LegWeight = new double[inst.Legs.Length],
        LegVolume = new double[inst.Legs.Length],
        FlightCover = new double[inst.Flights.Length],
        DepBalance = new double[inst.Fleets.Length, inst.Flights.Length],
        ArrBalance = new double[inst.Fleets.Length, inst.Flights.Length],
        FleetSize = new double[inst.Fleets.Length],
    };
}
