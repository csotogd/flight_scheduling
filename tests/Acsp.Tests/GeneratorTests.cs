using Acsp.Core;
using Acsp.Data;

namespace Acsp.Tests;

public class GeneratorTests
{
    [Theory]
    [InlineData("RC", 1, 70, 30, 0)]
    [InlineData("RC", 2, 70, 60, 0)]
    [InlineData("RC", 3, 35, 95, 0)]
    [InlineData("IC", 1, 60, 30, 220)]
    [InlineData("IC", 2, 60, 60, 220)]
    [InlineData("IC", 3, 30, 90, 220)]
    [InlineData("MI", 1, 70, 30, 300)]
    [InlineData("MI", 3, 35, 95, 300)]
    [InlineData("EX", 1, 720, 180, 0)]
    [InlineData("EX", 3, 360, 720, 0)]
    public void Flight_counts_match_table2(string airline, int set, int mand, int opt, int extMax)
    {
        var inst = InstanceGenerator.Generate(airline, set, seed: 1);
        Assert.Equal(mand, inst.MandatoryFlights.Count());
        Assert.Equal(opt, inst.OptionalFlights.Count());
        Assert.InRange(inst.ExternalFlights.Count(), 0, extMax);
        if (extMax > 0)
            Assert.True(inst.ExternalFlights.Count() >= extMax * 2 / 3,
                $"expected roughly {extMax} external flights, got {inst.ExternalFlights.Count()}");
    }

    [Theory]
    [InlineData("RC", 800)]
    [InlineData("IC", 3500)]
    [InlineData("MI", 4000)]
    [InlineData("EX", 6000)]
    public void Od_counts_match_table2(string airline, int ods)
    {
        var inst = InstanceGenerator.Generate(airline, 1, seed: 2);
        Assert.Equal(ods, inst.Ods.Length);
    }

    [Fact]
    public void SetII_extends_SetI_and_SetIII_reuses_flights()
    {
        var i1 = InstanceGenerator.Generate("RC", 1, 7);
        var i2 = InstanceGenerator.Generate("RC", 2, 7);
        var i3 = InstanceGenerator.Generate("RC", 3, 7);
        var codes1 = i1.Flights.Select(f => f.Code).ToHashSet();
        var codes2 = i2.Flights.Select(f => f.Code).ToHashSet();
        var codes3 = i3.Flights.Select(f => f.Code).ToHashSet();
        Assert.Subset(codes2, codes1);
        Assert.Equal(codes2, codes3);
        // III flips half the mandatory flights to optional but keeps them in the pool
        Assert.Equal(35, i3.MandatoryFlights.Count());
    }

    [Fact]
    public void Every_cargo_flight_is_compatible_with_some_fleet()
    {
        foreach (var airline in new[] { "RC", "IC", "MI", "EX" })
        {
            var inst = InstanceGenerator.Generate(airline, 2, seed: 3);
            foreach (var f in inst.CargoFlights)
                Assert.True(inst.Fleets.Any(k => inst.Compatible(k.Id, f.Id)),
                    $"{airline}: flight {f.Code} incompatible with every fleet");
        }
    }

    [Fact]
    public void Generation_is_deterministic()
    {
        var a = InstanceGenerator.Generate("IC", 2, 42);
        var b = InstanceGenerator.Generate("IC", 2, 42);
        Assert.Equal(a.Legs.Length, b.Legs.Length);
        Assert.Equal(a.Ods.Sum(o => o.Weight), b.Ods.Sum(o => o.Weight), 9);
        Assert.Equal(
            a.Flights.Select(f => f.Code + ":" + string.Join(',', f.LegIds)),
            b.Flights.Select(f => f.Code + ":" + string.Join(',', f.LegIds)));
    }

    [Fact]
    public void Revenue_tkm_is_close_to_target()
    {
        var inst = InstanceGenerator.Generate("IC", 2, 5);
        var byId = inst.Airports;
        double tkm = inst.Ods.Sum(o =>
            o.Weight * GreatCircle.Km(byId[o.Origin].Lat, byId[o.Origin].Lon,
                                      byId[o.Destination].Lat, byId[o.Destination].Lon));
        Assert.InRange(tkm, 0.9 * 90_962_599, 1.1 * 90_962_599);
    }

    [Fact]
    public void Roundtrip_through_json_preserves_instance()
    {
        var inst = InstanceGenerator.Generate("RC", 1, 11);
        var path = Path.Combine(Path.GetTempPath(), $"acsp-test-{Guid.NewGuid():N}.json");
        try
        {
            InstanceJson.Save(inst, path);
            var back = InstanceJson.Load(path);
            Assert.Equal(inst.Name, back.Name);
            Assert.Equal(inst.Legs.Length, back.Legs.Length);
            Assert.Equal(inst.Ods.Length, back.Ods.Length);
            Assert.Equal(inst.Flights.Length, back.Flights.Length);
            Assert.Equal(inst.Ods.Sum(o => o.Rate), back.Ods.Sum(o => o.Rate), 6);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Some_ods_have_a_feasible_direct_or_onestop_path()
    {
        // Sanity: the network must actually be able to serve a decent share of the demand.
        var inst = InstanceGenerator.Generate("RC", 2, 13);
        int servable = 0;
        foreach (var od in inst.Ods)
        {
            bool found = false;
            foreach (var leg in inst.Legs)
            {
                if (leg.Origin != od.Origin) continue;
                if (leg.Destination == od.Destination)
                {
                    var p = new CargoPath { OdId = od.Id, LegIds = [leg.Id] };
                    if (p.IsFeasible(inst, out _)) { found = true; break; }
                }
                foreach (var leg2 in inst.Legs)
                {
                    if (leg2.Origin != leg.Destination || leg2.Destination != od.Destination) continue;
                    var p2 = new CargoPath { OdId = od.Id, LegIds = [leg.Id, leg2.Id] };
                    if (p2.IsFeasible(inst, out _)) { found = true; break; }
                }
                if (found) break;
            }
            if (found) servable++;
        }
        Assert.True(servable > inst.Ods.Length / 4,
            $"only {servable}/{inst.Ods.Length} ODs servable with <=1 transfer");
    }
}
