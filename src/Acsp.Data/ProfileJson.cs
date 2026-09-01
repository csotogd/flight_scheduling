using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acsp.Data;

/// <summary>
/// The airline configuration file: everything that defines an airline — hubs, fleet specs
/// (counts, capacities, costs, the payload-range curve), demand generation parameters,
/// curfews, cargo handling time, the service commitment — as an editable JSON document.
/// `profile export` dumps a built-in archetype as a starting point; `generate` accepts a
/// profile path anywhere it accepts a built-in code.
/// </summary>
public static class ProfileJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static void Save(AirlineProfile profile, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, Options));
    }

    public static AirlineProfile Load(string path)
    {
        var p = JsonSerializer.Deserialize<AirlineProfile>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException($"Cannot parse profile {path}");
        Validate(p, path);
        return p;
    }

    private static void Validate(AirlineProfile p, string path)
    {
        void Bad(string why) => throw new InvalidDataException($"{path}: {why}");
        if (string.IsNullOrWhiteSpace(p.Code)) Bad("code is required");
        if (p.HubCodes is not { Length: > 0 }) Bad("at least one hub is required");
        foreach (var h in p.HubCodes)
            if (AirportDb.All.All(a => a.Code != h)) Bad($"unknown hub airport '{h}'");
        if (p.Fleets is not { Length: > 0 }) Bad("at least one fleet is required");
        foreach (var f in p.Fleets)
        {
            if (f.Count <= 0 || f.MaxWeight <= 0 || f.RangeKm <= 0)
                Bad($"fleet {f.Code}: Count, MaxWeight and RangeKm must be positive");
            if (f.RangeMaxKm != 0 && f.RangeMaxKm < f.RangeKm)
                Bad($"fleet {f.Code}: RangeMaxKm must be >= RangeKm (or 0 for no curve)");
            if (f.PayloadAtMaxRangeT is < 0 || f.PayloadAtMaxRangeT > f.MaxWeight)
                Bad($"fleet {f.Code}: PayloadAtMaxRangeT must be in [0, MaxWeight]");
        }
        if (p.CurfewStart is < -1 or >= 1440 || p.CurfewEnd is < -1 or >= 1440)
            Bad("curfew minutes must be in [0, 1440) or -1 to disable");
        if (p.CargoHandlingMinutes < 0) Bad("cargoHandlingMinutes must be >= 0");
        if (p.MandatoryFlights < 0 || p.MinDeliveryDays < 1
            || p.MaxDeliveryDays < p.MinDeliveryDays)
            Bad("bad flight/delivery-day counts");
    }

    /// <summary>Built-in code, or a path to a profile JSON: both resolve to a profile.</summary>
    public static AirlineProfile Resolve(string codeOrPath) =>
        codeOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || File.Exists(codeOrPath)
            ? Load(codeOrPath)
            : AirlineProfile.Get(codeOrPath);
}
