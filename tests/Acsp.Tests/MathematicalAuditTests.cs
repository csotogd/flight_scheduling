using Acsp.Core;
using Acsp.Data;
using Acsp.Solver;
using Acsp.Solver.Lp;

namespace Acsp.Tests;

/// <summary>Independent small counterexamples from the October 2026 mathematical audit.</summary>
public class MathematicalAuditTests
{
    private static Airport Ap(int id, bool hub = false, bool curfew = false) => new()
    {
        Id = id, Code = $"A{id}", IsTransferHub = hub,
        MaintenanceHubFor = [hub], MaintenanceCost = [100],
        CurfewStart = curfew ? 0 : -1, CurfewEnd = curfew ? 360 : -1,
    };

    private static FleetType Fleet(int count = 1, int cycles = 20) => new()
    {
        Id = 0, Code = "K", Count = count, FixedCostPerAircraft = 100,
        MaxWeight = 20, MaxVolume = 100, RangeKm = 10000, DefaultMinGroundTime = 60,
        MaintenanceDuration = 60, MaxCyclesBetweenMaintenance = cycles,
        MaxElapsedMinutesBetweenMaintenance = 30000, MaxFlightMinutesBetweenMaintenance = 20000,
    };

    private static Leg L(int id, int f, int o, int d, int dep, int arr) => new()
    {
        Id = id, FlightId = f, Origin = o, Destination = d, Dep = dep, Arr = arr,
        DistanceKm = 100, VariableCostPerTonne = 0,
    };

    private static Flight F(int id, int[] legs, bool mandatory = true, double cost = 0) => new()
    {
        Id = id, Code = $"F{id}", LegIds = legs, IsMandatory = mandatory,
        IsExternal = false, FixedCostByFleet = [cost],
    };

    private static Instance Make(Airport[] airports, FleetType[] fleets, Leg[] legs,
        Flight[] flights, Od[]? ods = null)
    {
        var inst = new Instance
        {
            Name = "math-audit", Period = Period.Weekly, Airports = airports,
            Fleets = fleets, Legs = legs, Flights = flights, Ods = ods ?? [],
        };
        inst.Validate();
        return inst;
    }

    private static Instance RoundTrip(double cost = 0) => Make([Ap(0, true), Ap(1)], [Fleet()],
        [L(0, 0, 0, 1, 600, 800), L(1, 0, 1, 0, 900, 1100)], [F(0, [0, 1], cost: cost)]);

    private static BpcOptions Options(bool maintenance = false, string backend = "highs") => new()
    {
        LpBackend = backend, WithMaintenance = maintenance, GapTarget = 0,
        TimeLimitSeconds = 20, MipHeuristicFrequency = 0,
        ColGen = new() { ExactStringPricing = true, ParallelPricing = false },
    };

    [Theory]
    [InlineData(0, -837)]
    [InlineData(1, -537)]
    [InlineData(200, -537)]
    public void Certified_bound_dominates_optimum_with_capped_column_batches(int batch,
        double expectedObjective)
    {
        // Six two-leg flights, at most four cycles between maintenance checks:
        // at least three checks ($300), one aircraft ($100), flight costs $137.
        // Three chronological pairs achieve that lower cost bound: optimum = -537.
        double[] costs = [14, 34, 43, 9, 28, 9];
        var legs = new List<Leg>();
        var flights = new List<Flight>();
        for (int j = 0; j < costs.Length; j++)
        {
            int dep = 200 + j * 1200;
            legs.Add(L(2 * j, j, 0, 1, dep, dep + 200));
            legs.Add(L(2 * j + 1, j, 1, 0, dep + 300, dep + 500));
            flights.Add(F(j, [2 * j, 2 * j + 1], cost: costs[j]));
        }
        var inst = Make([Ap(0, true), Ap(1)], [Fleet(2, 4)], [.. legs], [.. flights]);
        var opt = Options(true) with
        {
            ColGen = new() { ExactStringPricing = true, MaxStringColumnsPerIteration = batch },
        };
        var result = new BranchAndPrice(inst, opt).Solve();
        Assert.NotNull(result.Best);
        Assert.Equal(expectedObjective, result.Objective, 5);
        Assert.True(result.BoundCertified);
        Assert.True(result.Bound >= -537 - 1e-6);
        Assert.True(FeasibilityChecker.Check(inst, result.Best!).IsFeasible);
        // With column additions disabled, the integral RMP is suboptimal. Its finite
        // Farley headroom must survive acceptance of that incumbent (never Gap = 0).
        if (batch == 0) Assert.True(result.Gap > 0.3);
    }

    [Fact]
    public void Maintenance_solve_rejects_seed_from_a_different_model()
    {
        var inst = RoundTrip();
        var seed = CoverConstructor.Build(inst).Solution!;
        Assert.False(seed.WithMaintenance);
        var result = new BranchAndPrice(inst, Options(true) with { SeedSolution = seed }).Solve();
        Assert.NotNull(result.Best);
        Assert.True(result.Best!.WithMaintenance);
        Assert.Equal(-200, result.Objective, 6);
        Assert.Equal(result.Best.Profit(inst), result.Objective, 6);
    }

    [Fact]
    public void Maintenance_design_keeps_the_requested_model()
    {
        var result = new NetworkDesigner(RoundTrip(), new DesignOptions
        {
            WithMaintenance = true, MaxRounds = 0, FinalTimeLimitSeconds = 0,
            RoundTimeLimitSeconds = 10, LpBackend = "highs",
        }).Run();
        Assert.NotNull(result.Best.Best);
        Assert.True(result.Best.Best!.WithMaintenance);
        Assert.Equal(-200, result.Best.Objective, 6);
    }

    [Fact]
    public void Heuristic_maintenance_pricing_does_not_report_a_certified_zero_gap()
    {
        var inst = RoundTrip();
        var result = new BranchAndPrice(inst, Options(true) with
        {
            ColGen = new() { ExactStringPricing = false },
        }).Solve();
        Assert.NotNull(result.Best);
        Assert.False(result.BoundCertified);
        Assert.Equal(double.PositiveInfinity, result.Bound);
        Assert.Equal(double.PositiveInfinity, result.Gap);
        // The result remains exportable when no finite bound was proved.
        string json = System.Text.Json.JsonSerializer.Serialize(SolutionJson.Build(inst, result));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(System.Text.Json.JsonValueKind.Null,
            doc.RootElement.GetProperty("stats").GetProperty("bound").ValueKind);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Multi_period_flight_requires_two_aircraft(int count, bool feasible)
    {
        // 200 + wait(800, 500) + 200 = 10180, NOT time(600, 700) = 100.
        var inst = Make([Ap(0, true), Ap(1)], [Fleet(count)],
            [L(0, 0, 0, 1, 600, 800), L(1, 0, 1, 0, 500, 700)], [F(0, [0, 1])]);
        Assert.Equal(10180, inst.FlightDuration(inst.Flights[0]));
        var result = new BranchAndPrice(inst, Options()).Solve();
        Assert.Equal(feasible, result.Best is not null);
        if (feasible)
        {
            Assert.Equal(2, Assert.Single(result.Best!.Rotations).AircraftNeeded(inst));
            Assert.Equal(-200, result.Objective, 6);
        }
        else
        {
            Assert.True(result.BoundCertified);
            Assert.Equal(double.NegativeInfinity, result.Bound);
        }
    }

    [Fact]
    public void Maintenance_pricing_connects_a_flight_starting_three_week_slots_earlier()
    {
        var inst = Make([Ap(0, true), Ap(1), Ap(2)], [Fleet(3)],
            [L(0, 0, 0, 1, 9000, 9200), L(1, 0, 1, 2, 9100, 9300),
             L(2, 0, 2, 1, 9200, 9400), L(3, 1, 1, 0, 100, 300)],
            [F(0, [0, 1, 2]), F(1, [3])]);
        var duals = MasterDuals.Zero(inst);
        duals.FlightCover[0] = duals.FlightCover[1] = -5000;
        var pricer = new StringPricer(inst, true, 0) { ExactMode = true };
        var priced = pricer.Price(duals, PricingRestrictions.AllowAll(inst));
        var pair = Assert.Single(priced.Where(s => s.Str.FlightIds.SequenceEqual(new[] { 0, 1 })));
        Assert.True(pair.Str.IsFeasible(inst, true, out var reason), reason);
        Assert.Equal(21540, pair.Str.ElapsedMinutes(inst));
        Assert.Equal(pricer.ReducedCost(pair.Str, duals), pair.ReducedCost, 6);
    }

    public static IEnumerable<object[]> Backends()
    {
        yield return ["highs"];
        if (CplexSolver.IsAvailable) yield return ["cplex"];
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Phase_one_generates_nontrivial_mandatory_maintenance_strings(string backend)
    {
        var inst = Make([Ap(0, true), Ap(1)], [Fleet()],
            [L(0, 0, 0, 1, 600, 800), L(1, 1, 1, 0, 900, 1100)],
            [F(0, [0], cost: 20000000), F(1, [1])]);
        var result = new BranchAndPrice(inst, Options(true, backend)).Solve();
        Assert.NotNull(result.Best);
        Assert.Equal(new[] { 0, 1 }, Assert.Single(result.Best!.SelectedStrings).FlightIds);
        Assert.Equal(-20000200, result.Objective, 4);
        Assert.Equal(result.Best.Profit(inst), result.Objective, 4);
        Assert.True(result.BoundCertified);
        Assert.True(result.Bound >= result.Objective - 1e-6);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(20000000)]
    public void Mandatory_flight_is_not_replaced_by_a_cheaper_artificial(double cost)
    {
        var inst = RoundTrip(cost);
        var result = new BranchAndPrice(inst, Options()).Solve();
        Assert.NotNull(result.Best);
        Assert.Equal(-cost - 100, result.Objective, 5);
        var direct = DirectMipSolver.Solve(inst, false, []);
        Assert.Equal(LpStatus.Optimal, direct.Status);
        Assert.NotNull(direct.Solution);
        Assert.Equal(result.Objective, direct.Objective, 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Curfew_is_enforced_before_selecting_flights(bool mandatory)
    {
        var inst = Make([Ap(0, true), Ap(1, curfew: true)], [Fleet()],
            [L(0, 0, 0, 1, 100, 200), L(1, 0, 1, 0, 300, 400)],
            [F(0, [0, 1], mandatory)],
            [new Od { Id = 0, Origin = 0, Destination = 1, Avail = 0,
                MaxDeliveryTime = 1000, Weight = 10, Volume = 10, Rate = 100 }]);
        Assert.False(inst.Compatible(0, 0));
        var result = new BranchAndPrice(inst, Options()).Solve();
        if (mandatory) Assert.Null(result.Best);
        else
        {
            Assert.NotNull(result.Best);
            Assert.Empty(result.Best!.SelectedStrings);
            Assert.Equal(0, result.Objective, 6);
        }
    }

    [Fact]
    public void Interrupted_phase_one_never_returns_a_monetary_bound_or_incumbent()
    {
        var inst = Make([Ap(0, true), Ap(1)], [Fleet()],
            [L(0, 0, 0, 1, 600, 800), L(1, 1, 1, 0, 900, 1100)], [F(0, [0]), F(1, [1])]);
        using var rmp = new Rmp(inst, true, new HighsSolver());
        rmp.SeedTrivialStrings();
        var result = new ColumnGeneration(inst, rmp, Options(true).ColGen)
            .SolveNode(PricingRestrictions.AllowAll(inst), deadline: () => true);
        Assert.True(result.DeadlineHit);
        Assert.Equal(double.PositiveInfinity, result.DualBound);
        Assert.False(rmp.IsIntegral(result.Lp));
    }

    [Fact]
    public void Phase_one_completion_at_iteration_limit_restores_profit_objective()
    {
        var inst = Make([Ap(0, true), Ap(1)], [Fleet()],
            [L(0, 0, 0, 1, 600, 800), L(1, 1, 1, 0, 900, 1100)], [F(0, [0]), F(1, [1])]);
        var result = new BranchAndPrice(inst, Options(true) with
        {
            ColGen = new() { ExactStringPricing = true, MaxIterations = 1 },
        }).Solve();
        Assert.NotNull(result.Best);
        Assert.Equal(-200, result.Objective, 6);
        Assert.Equal(double.PositiveInfinity, result.Bound);
    }

    [Fact]
    public void Phase_one_can_be_reentered_and_restored_between_branch_nodes()
    {
        var inst = RoundTrip(20000000);
        using var rmp = new Rmp(inst, false, new HighsSolver());
        rmp.SeedTrivialStrings();
        var cg = new ColumnGeneration(inst, rmp, Options().ColGen);
        var root = PricingRestrictions.AllowAll(inst);
        Assert.Equal(-20000100, cg.SolveNode(root).Lp.Objective, 5);
        var excluded = root.Clone();
        excluded.ExcludeFlight(inst, 0);
        rmp.ApplyBranchingState(excluded);
        var infeasible = cg.SolveNode(excluded);
        Assert.True(infeasible.BoundCertified);
        Assert.Equal(double.NegativeInfinity, infeasible.DualBound);
        rmp.ApplyBranchingState(root);
        var restored = cg.SolveNode(root);
        Assert.False(rmp.IsPhaseOne);
        Assert.Equal(0, rmp.ArtificialUsage(restored.Lp), 6);
        Assert.Equal(-20000100, restored.Lp.Objective, 5);
    }

    [Fact]
    public void Phase_one_path_pricing_ignores_transport_transfer_and_storage_costs()
    {
        var inst = TestInstances.Small();
        var duals = MasterDuals.Zero(inst);
        duals.ObjectiveScale = 0;
        Array.Fill(duals.OdDemand, -1000);
        foreach (var leg in inst.Legs) duals.LegWeight[leg.Id] = 2 * leg.Id;
        var result = new PathPricer(inst).PriceBound(duals, PricingRestrictions.AllowAll(inst));
        Assert.True(result.Complete);
        Assert.NotEmpty(result.Paths);
        foreach (var priced in result.Paths)
        {
            double expected = 1000 - priced.Path.LegIds.Sum(l => duals.LegWeight[l]);
            Assert.Equal(expected, priced.ReducedCost, 6);
        }
    }
}
