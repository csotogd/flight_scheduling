using Acsp.Data;

namespace Acsp.Tests;

public class InstanceXlsxTests
{
    [Fact]
    public void Roundtrips_a_generated_instance()
    {
        var inst = InstanceGenerator.Generate("RC", 1, 1);
        var bytes = InstanceXlsx.Build(inst);
        var res = InstanceXlsx.Read(bytes, inst.Name + "-rt");
        Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => e.Text)));
        var rt = res.Instance!;
        Assert.Equal(inst.Airports.Length, rt.Airports.Length);
        Assert.Equal(inst.Fleets.Length, rt.Fleets.Length);
        Assert.Equal(inst.Flights.Length, rt.Flights.Length);
        Assert.Equal(inst.Legs.Length, rt.Legs.Length);
        Assert.Equal(inst.Ods.Length, rt.Ods.Length);
        for (int i = 0; i < inst.Flights.Length; i++)
        {
            Assert.Equal(inst.Flights[i].Code, rt.Flights[i].Code);
            Assert.Equal(inst.Flights[i].IsMandatory, rt.Flights[i].IsMandatory);
            Assert.Equal(inst.Flights[i].IsExternal, rt.Flights[i].IsExternal);
            Assert.Equal(inst.Flights[i].LegIds.Length, rt.Flights[i].LegIds.Length);
        }
        for (int i = 0; i < inst.Legs.Length; i++)
        {
            Assert.Equal(inst.Legs[i].Dep, rt.Legs[i].Dep);
            Assert.Equal(inst.Legs[i].Arr, rt.Legs[i].Arr);
            Assert.Equal(inst.Legs[i].Origin, rt.Legs[i].Origin);
            Assert.Equal(inst.Legs[i].Destination, rt.Legs[i].Destination);
        }
        for (int i = 0; i < inst.Ods.Length; i++)
        {
            Assert.Equal(inst.Ods[i].Weight, rt.Ods[i].Weight, 3);
            Assert.Equal(inst.Ods[i].Rate, rt.Ods[i].Rate, 3);
            Assert.Equal(inst.Ods[i].Avail, rt.Ods[i].Avail);
        }
        // fixed costs survive the round trip exactly (no estimation warnings for cargo flights)
        Assert.DoesNotContain(res.Messages, mfe => mfe.Text.Contains("estimated"));
    }

    [Fact]
    public void Reports_row_level_errors()
    {
        var inst = InstanceGenerator.Generate("RC", 1, 1);
        var bytes = InstanceXlsx.Build(inst);
        // corrupt the workbook: rename an airport code referenced by flights
        var res0 = InstanceXlsx.Read(bytes, "x");
        Assert.True(res0.Ok);

        // a workbook with a bad time and an unknown airport must be rejected with row messages
        var bad = XlsxWriter.Build(
            ("Airports", new List<object?[]>
            {
                new object?[] { "Code", "Name", "Lat", "Lon", "Hub" },
                new object?[] { "AAA", "A", 10.0, 10.0, "yes" },
                new object?[] { "BBB", "B", 11.0, 11.0, "" },
            }),
            ("Fleets", new List<object?[]>
            {
                new object?[] { "Code", "Count", "MaxWeightT", "RangeKm" },
                new object?[] { "F1", 2, 100, 9000 },
            }),
            ("Flights", new List<object?[]>
            {
                new object?[] { "FlightCode", "Kind", "LegNo", "From", "To", "Dep", "Arr" },
                new object?[] { "X1", "mandatory", 1, "AAA", "ZZZ", "Mon 10:00", "Mon 14:00" },
                new object?[] { "X2", "mandatory", 1, "AAA", "BBB", "notatime", "Mon 14:00" },
            }),
            ("ODs", new List<object?[]>
            {
                new object?[] { "From", "To", "WeightT", "RatePerT" },
                new object?[] { "AAA", "BBB", 5, 1000 },
            }));
        var res = InstanceXlsx.Read(bad, "bad");
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Text.Contains("ZZZ"));
        Assert.Contains(res.Errors, e => e.Text.Contains("notatime"));
    }

    [Fact]
    public void Parses_day_time_format()
    {
        Assert.Equal(0, InstanceXlsx.ParseTime("Mon 00:00"));
        Assert.Equal(1 * 1440 + 22 * 60 + 40, InstanceXlsx.ParseTime("tue 22:40"));
        Assert.Equal(6 * 1440 + 23 * 60 + 59, InstanceXlsx.ParseTime("Sun 23:59"));
        Assert.Equal(123, InstanceXlsx.ParseTime("123"));
        Assert.Null(InstanceXlsx.ParseTime("Foo 10:00"));
        Assert.Null(InstanceXlsx.ParseTime("Mon 25:00"));
    }
}
