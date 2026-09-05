using Acsp.Core;
using Acsp.Solver;
using Acsp.Solver.Lp;

namespace Acsp.Tests;

/// <summary>
/// Regression tests for the 2026-09 audit fixes: pricer/master capacity consistency
/// (payload-range frontier), CargoHandlingMinutes surviving design-loop instance rebuilds,
/// and the certification semantics of the dual bound (a "certified" DualBound must upper-bound
/// the true optimum; anything the label-limited pricers cannot guarantee is flagged estimate).
/// </summary>
public class AuditRegressionTests
{
    /// <summary>Tiny() with the fleet's payload-range knee moved BELOW the leg distances,
    /// so PayloadAtKm(leg) &lt; MaxWeight and the capacity credit becomes distance-dependent.</summary>
    private static Instance TinyWithPayloadDecay()
    {
        var b = TestInstances.Tiny();
        var fleets = new[]
        {
            new FleetType
            {
                Id = 0, Code = "F", Count = 2, FixedCostPerAircraft = 1000,
                MaxWeight = 20, MaxVolume = 150, RangeKm = 1000, RangeMaxKm = 3000,
                PayloadAtMaxRangeT = 10, DefaultMinGroundTime = 60,
                MaxCyclesBetweenMaintenance = 10, MaxFlightMinutesBetweenMaintenance = 4000,
                MaxElapsedMinutesBetweenMaintenance = 3 * 10080, MaintenanceDuration = 480,
            },
        };
        return new Instance
        {
            Name = b.Name, Period = b.Period, DeliverAll = b.DeliverAll,
            CargoHandlingMinutes = b.CargoHandlingMinutes,
            Airports = b.Airports, Fleets = fleets, Legs = b.Legs, Flights = b.Flights,
            Ods = b.Ods,
        };
    }

    [Fact]
    public void String_pricer_capacity_credit_follows_payload_range_frontier()
    {
        var inst = TinyWithPayloadDecay();
        var pricer = new StringPricer(inst, withMaintenance: false, countTime: 0);
        var s = new FlightString { FleetId = 0, FlightIds = [0] }; // F0: two 2000 km legs
        var zero = MasterDuals.Zero(inst);
        var duals = MasterDuals.Zero(inst);
        duals.LegWeight[0] = 1.0;
        // isolate the leg-0 capacity term: it must be the payload the airframe can actually
        // lift over 2000 km (15 t here) — the master column's coefficient (Rmp.AddString) —
        // and not the flat MaxWeight (20 t) the master never uses on long legs
        double credit = pricer.ReducedCost(s, duals) - pricer.ReducedCost(s, zero);
        Assert.Equal(inst.Fleets[0].PayloadAtKm(2000), credit, 6);
        Assert.Equal(15.0, credit, 6);
    }

    [Fact]
    public void Design_rebuilds_preserve_cargo_handling_minutes()
    {
        var b = TestInstances.Tiny();
        var inst = new Instance
        {
            Name = b.Name, Period = b.Period, DeliverAll = b.DeliverAll,
            CargoHandlingMinutes = 30,
            Airports = b.Airports, Fleets = b.Fleets, Legs = b.Legs, Flights = b.Flights,
            Ods = b.Ods,
        };
        // every design operation that rebuilds the Instance must carry the handling time —
        // dropping it to 0 makes connections easier than configured and biased the designed
        // networks against the (correctly handled) baseline
        var removed = NetworkDesigner.RemoveFlights(inst, new HashSet<string> { "F1" });
        Assert.Equal(30, removed.CargoHandlingMinutes);
        var coarse = OdConsolidator.Consolidate(inst).Coarse;
        Assert.Equal(30, coarse.CargoHandlingMinutes);
    }

    [Fact]
    public void Converged_dual_bound_is_certified_and_valid_without_maintenance()
    {
        var inst = TestInstances.Small();
        using var rmp = new Rmp(inst, withMaintenance: false, LpSolverFactory.Create("highs"));
        rmp.SeedTrivialStrings();
        var colgen = new ColumnGeneration(inst, rmp, new ColGenOptions());
        var result = colgen.SolveNode(PricingRestrictions.AllowAll(inst));
        Assert.Equal(LpStatus.Optimal, result.Lp.Status);
        Assert.True(result.BoundCertified, "no-maintenance converged bound must be certified");
        Assert.True(result.DualBound >= result.Lp.Objective - 1e-6,
            $"bound {result.DualBound:F4} below the converged LP {result.Lp.Objective:F4}");
        Assert.True(result.DualBound <=
            result.Lp.Objective + Math.Max(1.0, 0.01 * Math.Abs(result.Lp.Objective)),
            $"certified bound {result.DualBound:F2} orders above LP {result.Lp.Objective:F2}");
    }

    [Fact]
    public void Maintenance_bound_is_estimate_unless_string_pricing_exact()
    {
        var inst = TestInstances.Small();
        using var rmpH = new Rmp(inst, withMaintenance: true, LpSolverFactory.Create("highs"));
        rmpH.SeedTrivialStrings();
        var heuristic = new ColumnGeneration(inst, rmpH, new ColGenOptions())
            .SolveNode(PricingRestrictions.AllowAll(inst));
        Assert.False(heuristic.BoundCertified,
            "label-limited PRICE-S cannot certify a bound (chi=0 strings, sigma cap)");

        using var rmpX = new Rmp(inst, withMaintenance: true, LpSolverFactory.Create("highs"));
        rmpX.SeedTrivialStrings();
        var exact = new ColumnGeneration(inst, rmpX,
            new ColGenOptions { ExactStringPricing = true })
            .SolveNode(PricingRestrictions.AllowAll(inst));
        Assert.Equal(LpStatus.Optimal, exact.Lp.Status);
        Assert.True(exact.BoundCertified);

        // a certified maintenance bound must upper-bound the exhaustive-column MIP optimum
        var paths = DirectMipSolver.EnumeratePaths(inst, maxLegs: 5).ToList();
        var strings = BruteForce.AllFeasibleStrings(inst, withMaintenance: true, maxFlights: 5);
        var direct = DirectMipSolver.Solve(inst, withMaintenance: true, paths, strings);
        Assert.Equal(LpStatus.Optimal, direct.Status);
        Assert.True(exact.DualBound >= direct.Objective - 1e-4,
            $"certified maintenance bound {exact.DualBound:F2} sits below the " +
            $"direct-MIP optimum {direct.Objective:F2} — the bound is not valid");
    }
}
