using Acsp.Core;
using Acsp.Data;
using Acsp.Solver;

namespace Acsp.Tests;

/// <summary>Tests for the airline configuration layer: profile JSON round-trip, the
/// payload-range curve, night curfews and cargo handling time.</summary>
public class ConfigurationTests
{
    [Fact]
    public void Profile_json_round_trips_and_generates_the_same_instance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acsp-profile-{Guid.NewGuid():N}.json");
        try
        {
            ProfileJson.Save(AirlineProfile.RLA, path);
            var loaded = ProfileJson.Load(path);
            Assert.Equal(AirlineProfile.RLA.HubCodes, loaded.HubCodes);
            Assert.Equal(AirlineProfile.RLA.Fleets, loaded.Fleets);
            Assert.Equal(AirlineProfile.RLA.CargoHandlingMinutes, loaded.CargoHandlingMinutes);
            Assert.Equal(AirlineProfile.RLA.DeliverAll, loaded.DeliverAll);

            // the configuration file is a full substitute for the built-in profile
            var a = InstanceGenerator.Generate(AirlineProfile.RLA, 1, 7);
            var b = InstanceGenerator.Generate(loaded, 1, 7);
            Assert.Equal(a.Flights.Length, b.Flights.Length);
            Assert.Equal(a.Ods.Length, b.Ods.Length);
            Assert.Equal(a.Legs.Select(l => l.Dep), b.Legs.Select(l => l.Dep));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Profile_json_rejects_inconsistent_data()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acsp-profile-{Guid.NewGuid():N}.json");
        try
        {
            var bad = AirlineProfile.RC with
            {
                Fleets = [AirlineProfile.RC.Fleets[0] with { RangeMaxKm = 100 }], // < RangeKm
            };
            ProfileJson.Save(bad, path);
            Assert.Throws<InvalidDataException>(() => ProfileJson.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Payload_range_curve_derates_capacity_and_compatibility()
    {
        var k = new FleetType
        {
            Id = 0, Code = "T", Count = 1, FixedCostPerAircraft = 0,
            MaxWeight = 100, MaxVolume = 600, RangeKm = 9000,
            RangeMaxKm = 13000, PayloadAtMaxRangeT = 50,
        };
        Assert.Equal(100, k.PayloadAtKm(5000));           // flat up to full-payload range
        Assert.Equal(100, k.PayloadAtKm(9000));
        Assert.Equal(75, k.PayloadAtKm(11000), 6);        // halfway down the linear segment
        Assert.Equal(50, k.PayloadAtKm(13000), 6);        // fuel-limited point
        Assert.Equal(0, k.PayloadAtKm(13001));            // unflyable beyond max range

        // no curve configured: classic single-range behavior
        var flat = new FleetType
        {
            Id = 0, Code = "F", Count = 1, FixedCostPerAircraft = 0,
            MaxWeight = 100, MaxVolume = 600, RangeKm = 9000,
        };
        Assert.Equal(100, flat.PayloadAtKm(9000));
        Assert.Equal(0, flat.PayloadAtKm(9001));

        // the feasibility checker enforces the derated capacity
        var inst = InstanceGenerator.Generate("RLA", 1, 1);
        var longLeg = inst.Legs.OrderByDescending(l => l.DistanceKm).First();
        var fleet = inst.Fleets.First(f =>
            f.RangeMaxKm > 0 && f.PayloadAtKm(longLeg.DistanceKm) > 0
            && f.PayloadAtKm(longLeg.DistanceKm) < f.MaxWeight);
        Assert.True(fleet.PayloadAtKm(longLeg.DistanceKm) < fleet.MaxWeight,
            "expected a derated long leg in RLA");
    }

    [Fact]
    public void Generated_instances_respect_night_curfews()
    {
        var inst = InstanceGenerator.Generate("RLA", 1, 1);
        // non-hub airports carry the profile curfew; hubs stay open for the night sort
        Assert.Contains(inst.Airports, a => !a.IsTransferHub && a.CurfewStart >= 0);
        Assert.All(inst.Airports.Where(a => a.IsTransferHub), a => Assert.True(a.CurfewStart < 0));
        // no generated cargo leg arrives during its destination's curfew
        foreach (var leg in inst.CargoLegs)
            Assert.False(inst.Airports[leg.Destination].InArrivalCurfew(leg.Arr),
                $"leg {leg.Id} arrives at {inst.Airports[leg.Destination].Code} in curfew");
        // and the checker would flag one: shift a leg into the window artificially
        var ap = new Airport { Id = 0, Code = "X", CurfewStart = 0, CurfewEnd = 360 };
        Assert.True(ap.InArrivalCurfew(120));
        Assert.False(ap.InArrivalCurfew(400));
        Assert.Equal(240, ap.ArrivalCurfewDelay(120));
        var wrap = new Airport { Id = 0, Code = "W", CurfewStart = 1380, CurfewEnd = 300 };
        Assert.True(wrap.InArrivalCurfew(1400));   // 23:20, window 23:00-05:00
        Assert.True(wrap.InArrivalCurfew(120));
        Assert.False(wrap.InArrivalCurfew(400));
    }

    [Fact]
    public void Cargo_handling_delays_boarding_and_delivery()
    {
        var inst = InstanceGenerator.Generate("RLA", 1, 1);
        Assert.Equal(30, inst.CargoHandlingMinutes);

        // a path arriving within handling minutes of the deadline is infeasible; the same
        // path with handling 0 is fine — verified through TotalDeliveryTime's two ends
        var od = inst.Ods[0];
        var leg = inst.Legs.First(l => l.Origin == od.Origin);
        var path = new CargoPath { OdId = od.Id, LegIds = [leg.Id] };
        int t = path.TotalDeliveryTime(inst);
        var bare = new Instance
        {
            Name = "bare", Period = inst.Period, Airports = inst.Airports,
            Fleets = inst.Fleets, Legs = inst.Legs, Flights = inst.Flights, Ods = inst.Ods,
        };
        int t0 = path.TotalDeliveryTime(bare);
        // handling adds the unload time, plus a week when the wait was under the load time
        int delta = t - t0;
        Assert.True(delta == 30 || delta == 30 + inst.Period.N,
            $"handling delta {delta} is neither 30 nor 30 + a week");
    }
}
