using Acsp.Core;
using Acsp.Solver;
using Acsp.Solver.Lp;

namespace Acsp.Tests;

/// <summary>
/// Regression tests for the 2026-09-16 audit fixes: the final bound of an exhausted tree must
/// dominate every gap-target prune (never collapse to the incumbent), the maintenance string
/// pricer must not overflow on unconstrained elapsed limits, must reach two-week-slot
/// connections behind period-crossing predecessors, and must never emit a string the master
/// reconstructs differently (wait == N); week-wrapping rotation connections are feasible and
/// charged one extra aircraft; negative implied-bound-cut duals uncertify the bound pass.
/// </summary>
public class AuditRegression2Tests
{
    // ---------------------------------------------------------------- string pricer

    private static Instance TinyWithUnconstrainedElapsed()
    {
        var b = TestInstances.Tiny();
        var fleets = new[]
        {
            new FleetType
            {
                Id = 0, Code = "F", Count = 2, FixedCostPerAircraft = 1000,
                MaxWeight = 20, MaxVolume = 150, RangeKm = 10000, DefaultMinGroundTime = 60,
                MaxCyclesBetweenMaintenance = 10, MaxFlightMinutesBetweenMaintenance = 4000,
                // the documented "unconstrained" sentinel that used to overflow the
                // week-window arithmetic into a negative node count
                MaxElapsedMinutesBetweenMaintenance = int.MaxValue, MaintenanceDuration = 480,
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
    public void Unconstrained_elapsed_maintenance_still_prices_strings()
    {
        var inst = TinyWithUnconstrainedElapsed();
        var pricer = new StringPricer(inst, withMaintenance: true, countTime: 0);
        // the 6-week cap now truncates a window this limit cannot fill: not certifiable
        Assert.False(pricer.WeekWindowComplete);
        var duals = MasterDuals.Zero(inst);
        duals.FlightCover[0] = -100_000; // huge credit for covering F0 (HUB->AAA->HUB)
        var found = pricer.Price(duals, PricingRestrictions.AllowAll(inst));
        Assert.NotEmpty(found); // pre-fix: _nWeeks went negative and NOTHING was priced
    }

    /// <summary>HUB (maintenance hub) and AAA. G: HUB-&gt;AAA crossing the period boundary
    /// (dep 9000, arr 420). H: AAA-&gt;HUB dep 300 — earlier in the week than G arrives, so the
    /// connection needs the predecessor two week-slots back (wait 9960). H2: AAA-&gt;HUB dep 420
    /// — exactly G's arrival time-of-week, so the only candidate waits are 0 and a full
    /// period, both of which must be rejected.</summary>
    private static Instance WrapConnectionInstance()
    {
        const int nFleets = 1;
        var airports = new[]
        {
            new Airport { Id = 0, Code = "HUB", IsTransferHub = true, MinTransferTime = 60,
                MaintenanceHubFor = [true], MaintenanceCost = [500.0], MinGroundTimeOverride = [-1] },
            new Airport { Id = 1, Code = "AAA", MaintenanceHubFor = new bool[nFleets],
                MaintenanceCost = new double[nFleets], MinGroundTimeOverride = [-1] },
        };
        var fleets = new[]
        {
            new FleetType
            {
                Id = 0, Code = "F", Count = 2, FixedCostPerAircraft = 1000,
                MaxWeight = 20, MaxVolume = 150, RangeKm = 10000, DefaultMinGroundTime = 60,
                MaxCyclesBetweenMaintenance = 10, MaxFlightMinutesBetweenMaintenance = 4000,
                MaxElapsedMinutesBetweenMaintenance = 3 * 10080, MaintenanceDuration = 480,
            },
        };
        var legs = new[]
        {
            new Leg { Id = 0, FlightId = 0, Origin = 0, Destination = 1, Dep = 9000, Arr = 420,
                DistanceKm = 2000, VariableCostPerTonne = 40 },
            new Leg { Id = 1, FlightId = 1, Origin = 1, Destination = 0, Dep = 300, Arr = 600,
                DistanceKm = 2000, VariableCostPerTonne = 40 },
            new Leg { Id = 2, FlightId = 2, Origin = 1, Destination = 0, Dep = 420, Arr = 720,
                DistanceKm = 2000, VariableCostPerTonne = 40 },
        };
        var flights = new[]
        {
            new Flight { Id = 0, Code = "G", LegIds = [0], IsExternal = false, IsMandatory = false,
                FixedCostByFleet = [800.0] },
            new Flight { Id = 1, Code = "H", LegIds = [1], IsExternal = false, IsMandatory = false,
                FixedCostByFleet = [800.0] },
            new Flight { Id = 2, Code = "H2", LegIds = [2], IsExternal = false, IsMandatory = false,
                FixedCostByFleet = [800.0] },
        };
        var ods = new[]
        {
            new Od { Id = 0, Origin = 0, Destination = 1, Avail = 0, MaxDeliveryTime = 8000,
                Weight = 5, Volume = 30, Rate = 300 },
        };
        var inst = new Instance
        {
            Name = "wrap", Period = Period.Weekly,
            Airports = airports, Fleets = fleets, Legs = legs, Flights = flights, Ods = ods,
        };
        inst.Validate();
        return inst;
    }

    [Fact]
    public void Period_crossing_predecessor_reaches_two_slot_connection()
    {
        var inst = WrapConnectionInstance();
        var pricer = new StringPricer(inst, withMaintenance: true, countTime: 0)
        { ExactMode = true };
        var duals = MasterDuals.Zero(inst);
        duals.FlightCover[0] = -50_000;
        duals.FlightCover[1] = -50_000;
        duals.FlightCover[2] = -50_000;
        var found = pricer.Price(duals, PricingRestrictions.AllowAll(inst), maxColumns: 1000);
        // G arrives AAA at time-of-week 420 having crossed the boundary; H departs at 300,
        // i.e. one 9960-minute wait later — a connection that lives two week-slots after
        // G's departure slot. Pre-fix the {week-1, week} window could never reach it, so
        // ExactMode was not exact and maintenance Farley bounds could be invalid.
        Assert.Contains(found, c => c.Str.FlightIds.SequenceEqual(new[] { 0, 1 }));
    }

    [Fact]
    public void Priced_strings_always_survive_master_reconstruction()
    {
        var inst = WrapConnectionInstance();
        var pricer = new StringPricer(inst, withMaintenance: true, countTime: 0)
        { ExactMode = true };
        var duals = MasterDuals.Zero(inst);
        duals.FlightCover[0] = -50_000;
        duals.FlightCover[1] = -50_000;
        duals.FlightCover[2] = -50_000;
        var found = pricer.Price(duals, PricingRestrictions.AllowAll(inst), maxColumns: 1000);
        Assert.NotEmpty(found);
        // H2 departs exactly at G's arrival time-of-week: the only realizable waits are 0
        // and one full period. A wait == N label used to be accepted, but the master
        // reconstructs connections mod N (FlightString.ElapsedMinutes), reading it as a
        // zero-minute ground stop — every emitted string must survive that reconstruction
        foreach (var c in found)
            Assert.True(c.Str.IsFeasible(inst, withMaintenance: true, out var why),
                $"pricer emitted [{c.Str.Key()}]: {why}");
        Assert.DoesNotContain(found, c => c.Str.FlightIds.SequenceEqual(new[] { 0, 2 }));
    }

    // ---------------------------------------------------------------- bound reporting

    [Fact]
    public void Exhausted_tree_bound_dominates_gap_target_prunes()
    {
        var inst = TestInstances.Small();
        var paths = DirectMipSolver.EnumeratePaths(inst, maxLegs: 5).ToList();
        var direct = DirectMipSolver.Solve(inst, withMaintenance: false, paths, null);
        Assert.Equal(LpStatus.Optimal, direct.Status);
        Assert.NotNull(direct.Solution);

        // a wide gap target forces slack prunes; no MIP heuristic, so the incumbent comes
        // from integral nodes and need not be optimal when the tree exhausts
        var bpc = new BranchAndPrice(inst, new BpcOptions
        {
            GapTarget = 0.4, TimeLimitSeconds = 300,
            MipHeuristicFrequency = 0, LoadSeedFlows = false,
        });
        var res = bpc.Solve();
        Assert.NotNull(res.Best);
        // the reported bound must upper-bound the true optimum: pre-fix an exhausted tree
        // collapsed it to the incumbent (Gap = 0) although subtrees with provable headroom
        // up to incumbent * (1 + GapTarget) had been pruned
        Assert.True(res.Bound >= direct.Objective - 1e-4,
            $"reported bound {res.Bound:F2} below the true optimum {direct.Objective:F2}");
        // and a zero gap may only ever be claimed for an actually-optimal incumbent
        if (res.Gap < 1e-9) Assert.Equal(direct.Objective, res.Objective, 3);
    }

    // ---------------------------------------------------------------- periodic rotations

    /// <summary>One mandatory flight HUB-&gt;AAA-&gt;HUB whose self-rotation connection is a
    /// 40-minute mod-N gap — shorter than the 60-minute ground time, so the aircraft waits
    /// 40 + 10080 minutes and the rotation needs TWO airplanes (span 10040 + 10120).</summary>
    private static Instance TightWrapRotationInstance()
    {
        const int nFleets = 1;
        var airports = new[]
        {
            new Airport { Id = 0, Code = "HUB", IsTransferHub = true, MinTransferTime = 60,
                MaintenanceHubFor = [true], MaintenanceCost = [500.0], MinGroundTimeOverride = [-1] },
            new Airport { Id = 1, Code = "AAA", MaintenanceHubFor = new bool[nFleets],
                MaintenanceCost = new double[nFleets], MinGroundTimeOverride = [-1] },
        };
        var fleets = new[]
        {
            new FleetType
            {
                Id = 0, Code = "F", Count = 2, FixedCostPerAircraft = 1000,
                MaxWeight = 20, MaxVolume = 150, RangeKm = 10000, DefaultMinGroundTime = 60,
                MaxCyclesBetweenMaintenance = 10, MaxFlightMinutesBetweenMaintenance = 12000,
                MaxElapsedMinutesBetweenMaintenance = 3 * 10080, MaintenanceDuration = 480,
            },
        };
        var legs = new[]
        {
            new Leg { Id = 0, FlightId = 0, Origin = 0, Destination = 1, Dep = 10, Arr = 5000,
                DistanceKm = 2000, VariableCostPerTonne = 40 },
            new Leg { Id = 1, FlightId = 0, Origin = 1, Destination = 0, Dep = 5100, Arr = 10050,
                DistanceKm = 2000, VariableCostPerTonne = 40 },
        };
        var flights = new[]
        {
            new Flight { Id = 0, Code = "W", LegIds = [0, 1], IsExternal = false,
                IsMandatory = true, FixedCostByFleet = [800.0] },
        };
        var ods = new[]
        {
            new Od { Id = 0, Origin = 0, Destination = 1, Avail = 0, MaxDeliveryTime = 8000,
                Weight = 2, Volume = 10, Rate = 300 },
        };
        var inst = new Instance
        {
            Name = "tight-wrap", Period = Period.Weekly,
            Airports = airports, Fleets = fleets, Legs = legs, Flights = flights, Ods = ods,
        };
        inst.Validate();
        return inst;
    }

    [Fact]
    public void Week_wrapping_rotation_connection_is_feasible_and_charged()
    {
        var inst = TightWrapRotationInstance();
        var sol = new Solution
        {
            SelectedStrings = [new FlightString { FleetId = 0, FlightIds = [0] }],
            Flows = [], SelectedExternalFlights = [], WithMaintenance = false,
        };
        SolutionAssembler.AssembleRotations(inst, sol);
        var rot = Assert.Single(sol.Rotations);
        // conn = Time(10050, 10) = 40 < 60 required: the periodic model waits 40 + N —
        // exactly what the master's ground-arc chain charges via the extra chi crossing.
        // Pre-fix TotalMinutes used the bare mod-N gap (one aircraft short) and the
        // checker rejected the pairing outright as RP-3-GROUND.
        Assert.Equal(2 * (long)inst.Period.N, rot.TotalMinutes(inst));
        Assert.Equal(2, rot.AircraftNeeded(inst));
        var report = FeasibilityChecker.Check(inst, sol);
        Assert.True(report.IsFeasible, report.ToString());
    }

    // ---------------------------------------------------------------- bound-pass certification

    [Fact]
    public void Negative_cut_dual_uncertifies_the_bound_pass()
    {
        var inst = TestInstances.Tiny();
        var pricer = new PathPricer(inst);
        var duals = MasterDuals.Zero(inst);
        var clean = pricer.PriceBound(duals, PricingRestrictions.AllowAll(inst));
        Assert.True(clean.Complete);
        // a negative implied-bound-cut dual credits future path cost that the duals-free
        // A* heuristic cannot anticipate — admissibility is gone, so the pass must report
        // itself incomplete exactly as it already does for negative capacity duals
        duals.ImpliedBoundCuts[(0, 1)] = -1e-3;
        var tainted = pricer.PriceBound(duals, PricingRestrictions.AllowAll(inst));
        Assert.False(tainted.Complete);
    }
}
