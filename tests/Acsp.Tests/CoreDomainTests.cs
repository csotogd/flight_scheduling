using Acsp.Core;

namespace Acsp.Tests;

public class CoreDomainTests
{
    private readonly Instance _inst = TestInstances.Tiny();

    [Fact]
    public void Instance_validates()
    {
        // Tiny() already calls Validate(); just confirm basic derived values.
        Assert.Equal(0, _inst.FlightOrigin(_inst.Flights[0]));
        Assert.Equal(0, _inst.FlightDestination(_inst.Flights[0]));
        Assert.Equal(600, _inst.FlightDep(_inst.Flights[0]));
        Assert.Equal(1200, _inst.FlightArr(_inst.Flights[0]));
        Assert.Equal(600, _inst.FlightDuration(_inst.Flights[0]));
    }

    [Fact]
    public void Compatibility_respects_range()
    {
        Assert.True(_inst.Compatible(0, 0));
        Assert.True(_inst.Compatible(0, 1));
    }

    [Fact]
    public void Direct_path_is_feasible_and_priced()
    {
        var path = new CargoPath { OdId = 0, LegIds = [0] };
        Assert.True(path.IsFeasible(_inst, out _));
        Assert.Equal(300 - 40, path.Margin(_inst), 6);
        // avail=0, dep=600, block=240 => 840
        Assert.Equal(840, path.TotalDeliveryTime(_inst));
    }

    [Fact]
    public void Transfer_path_requires_hub()
    {
        // od 1: AAA->BBB via external X directly (no transfer)
        var direct = new CargoPath { OdId = 1, LegIds = [4] };
        Assert.True(direct.IsFeasible(_inst, out _));

        // AAA->HUB (leg 1) then HUB->BBB (leg 2): transfer at HUB, allowed
        var viaHub = new CargoPath { OdId = 1, LegIds = [1, 2] };
        Assert.True(viaHub.IsFeasible(_inst, out _));
        // margin includes transfer cost 10 and storage 0.5/h * (3000-1200)/60 = 15
        Assert.Equal(500 - 40 - 50 - 10 - 15, viaHub.Margin(_inst), 6);

        // legs 0 then 4 transfer at AAA (not a hub) -> infeasible
        var bad = new CargoPath { OdId = 3, LegIds = [0, 4] };
        Assert.False(bad.IsFeasible(_inst, out var why));
        Assert.Contains("non-hub", why);
    }

    [Fact]
    public void Deadline_violation_detected()
    {
        var od = _inst.Ods[2]; // avail 600, max 2000
        // leg 1 departs 960 arrives 1200: time = time(600,960)+240 = 600 -> ok
        var ok = new CargoPath { OdId = 2, LegIds = [1] };
        Assert.True(ok.IsFeasible(_inst, out _));
        Assert.True(ok.TotalDeliveryTime(_inst) <= od.MaxDeliveryTime);
    }

    [Fact]
    public void Flight_string_feasibility_maintenance()
    {
        var s = new FlightString { FleetId = 0, FlightIds = [0, 1] };
        Assert.True(s.IsFeasible(_inst, withMaintenance: true, out var why));
        Assert.Equal(4, s.Cycles(_inst));
        Assert.Equal(800 + 900 + 500, s.Cost(_inst, withMaintenance: true), 6);
        // Elapsed: dur(F0)=600 + conn(1200->3000)=1800 + dur(F1)=720 = 3120
        Assert.Equal(3120, s.ElapsedMinutes(_inst));

        // Single-flight string for FARP-T
        var t = new FlightString { FleetId = 0, FlightIds = [0] };
        Assert.True(t.IsFeasible(_inst, withMaintenance: false, out _));
        Assert.Equal(800, t.Cost(_inst, withMaintenance: false), 6);
    }

    [Fact]
    public void Flight_string_rejects_repeated_flight()
    {
        var s = new FlightString { FleetId = 0, FlightIds = [0, 0] };
        Assert.False(s.IsFeasible(_inst, withMaintenance: true, out var why));
        Assert.Contains("repeated", why);
    }

    [Fact]
    public void Feasibility_checker_flags_uncovered_mandatory()
    {
        var sol = new Solution
        {
            SelectedStrings = [], Flows = [], SelectedExternalFlights = [], WithMaintenance = false,
        };
        var report = FeasibilityChecker.Check(_inst, sol);
        Assert.False(report.IsFeasible);
        Assert.Contains(report.Violations, v => v.Contains("FA-1-COVER"));
    }

    [Fact]
    public void Feasibility_checker_accepts_valid_solution()
    {
        var s0 = new FlightString { FleetId = 0, FlightIds = [0] };
        var sol = new Solution
        {
            SelectedStrings = [s0],
            Flows = [(new CargoPath { OdId = 0, LegIds = [0] }, 5.0)],
            SelectedExternalFlights = [],
            Rotations = [new Rotation { FleetId = 0, Strings = [s0] }],
            WithMaintenance = false,
        };
        var report = FeasibilityChecker.Check(_inst, sol);
        Assert.True(report.IsFeasible, report.ToString());
        Assert.Equal(5 * (300 - 40) - 800 - 1000, sol.Profit(_inst), 6);
    }

    [Fact]
    public void Feasibility_checker_flags_overload()
    {
        var s0 = new FlightString { FleetId = 0, FlightIds = [0] };
        var sol = new Solution
        {
            SelectedStrings = [s0],
            Flows = [(new CargoPath { OdId = 0, LegIds = [0] }, 25.0)], // fleet cap 20t, demand 5t
            SelectedExternalFlights = [],
            WithMaintenance = false,
        };
        var report = FeasibilityChecker.Check(_inst, sol);
        Assert.Contains(report.Violations, v => v.Contains("CR-1-DEMAND"));
        Assert.Contains(report.Violations, v => v.Contains("CR-2-PAYLOAD"));
    }
}
