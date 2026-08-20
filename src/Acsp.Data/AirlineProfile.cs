namespace Acsp.Data;

public sealed record FleetSpec(
    string Code, int Count, double MaxWeight, double MaxVolume, double RangeKm, double SpeedKmH,
    double FixedPerAircraftWeek, double FuelCostPerKm, double LandingFee, double MaintenanceCost,
    int MntCycles, int MntFlightMinutes, int MntElapsedMinutes, int MntDurationMinutes);

public sealed record ExternalSpec(
    string Kind, // "RFS" (road feeder) or "PAX" (belly capacity)
    int NumFlights, double SpeedKmH, double MaxRangeKm,
    double MinWeight, double MaxWeight, double VolumePerTonne);

/// <summary>Parameters describing one of the four airline archetypes of §9.1 (Table 1).</summary>
public sealed record AirlineProfile(
    string Code,
    string[] HubCodes,
    int NumCargoDestinations,      // incl. hubs
    Region[] Regions,              // where cargo destinations are drawn from
    FleetSpec[] Fleets,
    int MandatoryFlights,          // full pool (sets I & II)
    int OptionalFlightsSetII,      // set I uses half of these
    int MinStops, int MaxStops,    // en-route stops per flight
    double InterHubRouteProb,      // probability a route ends at a different hub / is a trunk route
    int NumOds,
    int MinDeliveryDays, int MaxDeliveryDays,
    double RevenueTkmTarget,       // weekly revenue tonne-km (Table 1)
    ExternalSpec? External,
    int NumExternalDestinations,
    double RateMultiplier = 1.0,  // express carriers charge a premium yield
    bool DenseDemand = false)     // point-to-point O&Ds from every station, not hub-anchored
{
    public static readonly AirlineProfile RC = new(
        Code: "RC", HubCodes: ["HKG"], NumCargoDestinations: 23,
        Regions: [Region.Asia],
        Fleets:
        [
            new FleetSpec("A300F", 9, 45, 320, 6600, 840, 60_000, 3.4, 2200, 12_000,
                MntCycles: 44, MntFlightMinutes: 4500, MntElapsedMinutes: 2 * 10080, MntDurationMinutes: 480),
        ],
        MandatoryFlights: 70, OptionalFlightsSetII: 60,
        MinStops: 1, MaxStops: 2, InterHubRouteProb: 0,
        NumOds: 800, MinDeliveryDays: 1, MaxDeliveryDays: 3,
        RevenueTkmTarget: 13_788_512,
        External: null, NumExternalDestinations: 0);

    public static readonly AirlineProfile IC = new(
        Code: "IC", HubCodes: ["LUX"], NumCargoDestinations: 69,
        Regions: [Region.Europe, Region.NorthAmerica, Region.SouthAmerica, Region.Asia,
                  Region.MiddleEast, Region.Africa, Region.Oceania],
        Fleets:
        [
            new FleetSpec("B747F", 16, 110, 740, 8200, 900, 120_000, 6.0, 4000, 25_000,
                MntCycles: 40, MntFlightMinutes: 5400, MntElapsedMinutes: 2 * 10080, MntDurationMinutes: 600),
        ],
        MandatoryFlights: 60, OptionalFlightsSetII: 60,
        MinStops: 2, MaxStops: 5, InterHubRouteProb: 0,
        NumOds: 3500, MinDeliveryDays: 2, MaxDeliveryDays: 7,
        RevenueTkmTarget: 90_962_599,
        External: new ExternalSpec("RFS", NumFlights: 220, SpeedKmH: 65, MaxRangeKm: 1500,
            MinWeight: 18, MaxWeight: 28, VolumePerTonne: 4.5),
        NumExternalDestinations: 77);

    public static readonly AirlineProfile MI = new(
        Code: "MI", HubCodes: ["SIN", "BRU"], NumCargoDestinations: 42,
        Regions: [Region.Asia, Region.Europe, Region.NorthAmerica, Region.MiddleEast, Region.Oceania],
        Fleets:
        [
            new FleetSpec("B747F", 13, 110, 740, 8200, 900, 120_000, 6.0, 4000, 25_000,
                MntCycles: 40, MntFlightMinutes: 5400, MntElapsedMinutes: 2 * 10080, MntDurationMinutes: 600),
            new FleetSpec("B757F", 10, 29, 239, 5800, 850, 45_000, 2.6, 1500, 10_000,
                MntCycles: 44, MntFlightMinutes: 4500, MntElapsedMinutes: 2 * 10080, MntDurationMinutes: 420),
        ],
        MandatoryFlights: 70, OptionalFlightsSetII: 60,
        MinStops: 2, MaxStops: 5, InterHubRouteProb: 0.35,
        NumOds: 4000, MinDeliveryDays: 2, MaxDeliveryDays: 7,
        RevenueTkmTarget: 111_849_662,
        External: new ExternalSpec("PAX", NumFlights: 300, SpeedKmH: 870, MaxRangeKm: 12000,
            MinWeight: 10, MaxWeight: 18, VolumePerTonne: 5.5),
        NumExternalDestinations: 81);

    public static readonly AirlineProfile EX = new(
        Code: "EX", HubCodes: ["BRU", "BAH", "PHL", "PTY"], NumCargoDestinations: 142,
        Regions: [Region.Europe, Region.NorthAmerica, Region.SouthAmerica, Region.Asia,
                  Region.MiddleEast, Region.Africa, Region.Oceania],
        Fleets:
        [
            new FleetSpec("B747F", 14, 110, 740, 8200, 900, 120_000, 6.0, 4000, 25_000,
                MntCycles: 40, MntFlightMinutes: 5400, MntElapsedMinutes: 2 * 10080, MntDurationMinutes: 600),
            new FleetSpec("B757F", 31, 29, 239, 5800, 850, 45_000, 2.6, 1500, 10_000,
                MntCycles: 44, MntFlightMinutes: 4500, MntElapsedMinutes: 2 * 10080, MntDurationMinutes: 420),
            new FleetSpec("B727F", 11, 20, 150, 4400, 820, 35_000, 2.8, 1300, 9_000,
                MntCycles: 48, MntFlightMinutes: 4200, MntElapsedMinutes: 2 * 10080, MntDurationMinutes: 420),
            new FleetSpec("A300F", 28, 45, 320, 6600, 840, 60_000, 3.4, 2200, 12_000,
                MntCycles: 44, MntFlightMinutes: 4500, MntElapsedMinutes: 2 * 10080, MntDurationMinutes: 480),
        ],
        MandatoryFlights: 720, OptionalFlightsSetII: 360,
        MinStops: 1, MaxStops: 2, InterHubRouteProb: 0.12,
        NumOds: 6000, MinDeliveryDays: 1, MaxDeliveryDays: 3,
        RevenueTkmTarget: 119_806_624,
        External: null, NumExternalDestinations: 0,
        RateMultiplier: 2.2);

    /// <summary>
    /// A global integrator archetype beyond the paper's four: GI-style hub network (LEJ, CVG,
    /// HKG, BAH), a larger heterogeneous fleet (140 aircraft), and a DENSE demand matrix —
    /// every station originates O&amp;Ds towards many destinations, not just hub-anchored flows.
    /// </summary>
    public static readonly AirlineProfile GI = new(
        Code: "GI", HubCodes: ["LEJ", "CVG", "HKG", "BAH"], NumCargoDestinations: 150,
        Regions: [Region.Europe, Region.NorthAmerica, Region.SouthAmerica, Region.Asia,
                  Region.MiddleEast, Region.Africa, Region.Oceania],
        Fleets:
        [
            new FleetSpec("B777F", 26, 102, 650, 9200, 900, 130_000, 5.5, 3800, 25_000,
                MntCycles: 40, MntFlightMinutes: 5400, MntElapsedMinutes: 2 * 10080, MntDurationMinutes: 600),
            new FleetSpec("B767F", 42, 52, 438, 6000, 850, 70_000, 3.6, 2500, 15_000,
                MntCycles: 44, MntFlightMinutes: 4800, MntElapsedMinutes: 2 * 10080, MntDurationMinutes: 480),
            new FleetSpec("A300F", 45, 45, 320, 6600, 840, 60_000, 3.4, 2200, 12_000,
                MntCycles: 44, MntFlightMinutes: 4500, MntElapsedMinutes: 2 * 10080, MntDurationMinutes: 480),
            new FleetSpec("B757F", 35, 29, 239, 5800, 850, 45_000, 2.6, 1500, 10_000,
                MntCycles: 44, MntFlightMinutes: 4500, MntElapsedMinutes: 2 * 10080, MntDurationMinutes: 420),
        ],
        MandatoryFlights: 1000, OptionalFlightsSetII: 500,
        MinStops: 1, MaxStops: 2, InterHubRouteProb: 0.15,
        NumOds: 9000, MinDeliveryDays: 2, MaxDeliveryDays: 5,
        RevenueTkmTarget: 210_000_000,
        External: null, NumExternalDestinations: 0,
        RateMultiplier: 2.2, DenseDemand: true);

    public static AirlineProfile Get(string code) => code.ToUpperInvariant() switch
    {
        "RC" => RC, "IC" => IC, "MI" => MI, "EX" => EX, "GI" => GI,
        _ => throw new ArgumentException($"Unknown airline profile '{code}'"),
    };
}
