using System.Text.Json;
using System.Text.Json.Serialization;
using Acsp.Core;

namespace Acsp.Data;

/// <summary>JSON persistence for instances.</summary>
public static class InstanceJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        IncludeFields = false,
    };

    private sealed record Dto(
        string Name, int PeriodN, Airport[] Airports, FleetType[] Fleets,
        Leg[] Legs, Flight[] Flights, Od[] Ods);

    public static void Save(Instance inst, string path)
    {
        var dto = new Dto(inst.Name, inst.Period.N, inst.Airports, inst.Fleets,
            inst.Legs, inst.Flights, inst.Ods);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(dto, Options));
    }

    public static Instance Load(string path)
    {
        var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path), Options)
                  ?? throw new InvalidDataException($"Cannot parse {path}");
        var inst = new Instance
        {
            Name = dto.Name, Period = new Period(dto.PeriodN),
            Airports = dto.Airports, Fleets = dto.Fleets,
            Legs = dto.Legs, Flights = dto.Flights, Ods = dto.Ods,
        };
        inst.Validate();
        return inst;
    }
}
