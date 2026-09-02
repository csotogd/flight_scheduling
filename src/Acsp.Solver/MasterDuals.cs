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

    /// <summary>
    /// Convex combination alpha*center + (1-alpha)*current — the smoothed prices handed to
    /// the pricers under dual stabilization (Wentges smoothing). Cut duals appear in either
    /// operand or both; missing entries count as zero.
    /// </summary>
    public static MasterDuals Blend(MasterDuals center, MasterDuals current, double alpha)
    {
        double[] Mix(double[] a, double[] b)
        {
            var r = new double[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = alpha * a[i] + (1 - alpha) * b[i];
            return r;
        }
        double[,] Mix2(double[,] a, double[,] b)
        {
            var r = new double[a.GetLength(0), a.GetLength(1)];
            for (int i = 0; i < a.GetLength(0); i++)
                for (int j = 0; j < a.GetLength(1); j++)
                    r[i, j] = alpha * a[i, j] + (1 - alpha) * b[i, j];
            return r;
        }
        var cuts = new Dictionary<(int, int), double>();
        foreach (var key in center.ImpliedBoundCuts.Keys.Union(current.ImpliedBoundCuts.Keys))
            cuts[key] = alpha * center.ImpliedBoundCuts.GetValueOrDefault(key)
                + (1 - alpha) * current.ImpliedBoundCuts.GetValueOrDefault(key);
        return new MasterDuals
        {
            OdDemand = Mix(center.OdDemand, current.OdDemand),
            LegWeight = Mix(center.LegWeight, current.LegWeight),
            LegVolume = Mix(center.LegVolume, current.LegVolume),
            FlightCover = Mix(center.FlightCover, current.FlightCover),
            DepBalance = Mix2(center.DepBalance, current.DepBalance),
            ArrBalance = Mix2(center.ArrBalance, current.ArrBalance),
            FleetSize = Mix(center.FleetSize, current.FleetSize),
            ImpliedBoundCuts = cuts,
        };
    }
}
