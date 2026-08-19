using Acsp.Core;

namespace Acsp.Tests;

/// <summary>Small hand-built instances with known structure, shared across tests.</summary>
public static class TestInstances
{
    /// <summary>
    /// 3 airports: HUB (transfer + maintenance hub), AAA, BBB. One fleet (2 aircraft, 20t/150m3).
    /// Flight F0 (mandatory): HUB->AAA->HUB, dep 600. Flight F1 (optional): HUB->BBB->HUB, dep 3000.
    /// External flight X: AAA->BBB (10t/80m3), dep 1500.
    /// ODs: 0: HUB->AAA 5t; 1: AAA->BBB 3t (via external); 2: AAA->HUB 4t; 3: HUB->BBB 6t.
    /// </summary>
    public static Instance Tiny(bool f1Mandatory = false)
    {
        const int nFleets = 1;
        var airports = new[]
        {
            new Airport { Id = 0, Code = "HUB", IsTransferHub = true, MinTransferTime = 60,
                TransferCostPerTonne = 10, StorageCostPerTonneHour = 0.5,
                MaintenanceHubFor = [true], MaintenanceCost = [500.0], MinGroundTimeOverride = [-1] },
            new Airport { Id = 1, Code = "AAA", MaintenanceHubFor = new bool[nFleets],
                MaintenanceCost = new double[nFleets], MinGroundTimeOverride = [-1] },
            new Airport { Id = 2, Code = "BBB", MaintenanceHubFor = new bool[nFleets],
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
            // F0: HUB -> AAA -> HUB
            new Leg { Id = 0, FlightId = 0, Origin = 0, Destination = 1, Dep = 600, Arr = 840,
                DistanceKm = 2000, VariableCostPerTonne = 40 },
            new Leg { Id = 1, FlightId = 0, Origin = 1, Destination = 0, Dep = 960, Arr = 1200,
                DistanceKm = 2000, VariableCostPerTonne = 40 },
            // F1: HUB -> BBB -> HUB
            new Leg { Id = 2, FlightId = 1, Origin = 0, Destination = 2, Dep = 3000, Arr = 3300,
                DistanceKm = 2500, VariableCostPerTonne = 50 },
            new Leg { Id = 3, FlightId = 1, Origin = 2, Destination = 0, Dep = 3420, Arr = 3720,
                DistanceKm = 2500, VariableCostPerTonne = 50 },
            // X (external): AAA -> BBB
            new Leg { Id = 4, FlightId = 2, Origin = 1, Destination = 2, Dep = 1500, Arr = 1800,
                DistanceKm = 1800, VariableCostPerTonne = 60, MaxWeight = 10, MaxVolume = 80 },
        };
        var flights = new[]
        {
            new Flight { Id = 0, Code = "F0", LegIds = [0, 1], IsExternal = false, IsMandatory = true,
                FixedCostByFleet = [800.0] },
            new Flight { Id = 1, Code = "F1", LegIds = [2, 3], IsExternal = false, IsMandatory = f1Mandatory,
                FixedCostByFleet = [900.0] },
            new Flight { Id = 2, Code = "X", LegIds = [4], IsExternal = true },
        };
        var ods = new[]
        {
            new Od { Id = 0, Origin = 0, Destination = 1, Avail = 0, MaxDeliveryTime = 2000,
                Weight = 5, Volume = 30, Rate = 300 },
            new Od { Id = 1, Origin = 1, Destination = 2, Avail = 0, MaxDeliveryTime = 4000,
                Weight = 3, Volume = 20, Rate = 500 },
            new Od { Id = 2, Origin = 1, Destination = 0, Avail = 600, MaxDeliveryTime = 2000,
                Weight = 4, Volume = 25, Rate = 250 },
            new Od { Id = 3, Origin = 0, Destination = 2, Avail = 0, MaxDeliveryTime = 6000,
                Weight = 6, Volume = 40, Rate = 400 },
        };
        var inst = new Instance
        {
            Name = "tiny", Period = Period.Weekly,
            Airports = airports, Fleets = fleets, Legs = legs, Flights = flights, Ods = ods,
        };
        inst.Validate();
        return inst;
    }

    /// <summary>
    /// Richer hand-built instance: 4 airports (HUB is transfer+maintenance hub), 2 fleets
    /// (SML has range 2450 and cannot fly F1/F3), 5 cargo flights with staggered times,
    /// 5 O&amp;Ds. Small enough for exhaustive enumeration of strings up to 4 flights.
    /// </summary>
    public static Instance Small(bool allMandatory = false)
    {
        const int nFleets = 2;
        Airport Ap(int id, string code, bool hub) => new()
        {
            Id = id, Code = code, IsTransferHub = hub, MinTransferTime = hub ? 60 : 0,
            TransferCostPerTonne = hub ? 12 : 0, StorageCostPerTonneHour = hub ? 0.6 : 0,
            MaintenanceHubFor = Enumerable.Repeat(hub, nFleets).ToArray(),
            MaintenanceCost = hub ? [400.0, 250.0] : new double[nFleets],
            MinGroundTimeOverride = [-1, -1],
        };
        var airports = new[] { Ap(0, "HUB", true), Ap(1, "AAA", false), Ap(2, "BBB", false), Ap(3, "CCC", false) };
        var fleets = new[]
        {
            new FleetType
            {
                Id = 0, Code = "BIG", Count = 2, FixedCostPerAircraft = 1500,
                MaxWeight = 40, MaxVolume = 280, RangeKm = 8000, DefaultMinGroundTime = 60,
                MaxCyclesBetweenMaintenance = 12, MaxFlightMinutesBetweenMaintenance = 3000,
                MaxElapsedMinutesBetweenMaintenance = 2 * 10080, MaintenanceDuration = 480,
            },
            new FleetType
            {
                Id = 1, Code = "SML", Count = 3, FixedCostPerAircraft = 700,
                MaxWeight = 15, MaxVolume = 100, RangeKm = 2450, DefaultMinGroundTime = 60,
                MaxCyclesBetweenMaintenance = 12, MaxFlightMinutesBetweenMaintenance = 3000,
                MaxElapsedMinutesBetweenMaintenance = 2 * 10080, MaintenanceDuration = 300,
            },
        };
        Leg L(int id, int flight, int o, int d, int dep, int arr, double dist) => new()
        {
            Id = id, FlightId = flight, Origin = o, Destination = d, Dep = dep, Arr = arr,
            DistanceKm = dist, VariableCostPerTonne = Math.Round(dist * 0.03, 2),
        };
        var legs = new[]
        {
            L(0, 0, 0, 1, 600, 900, 2000), L(1, 0, 1, 0, 1020, 1320, 2000),          // F0 HUB-A-HUB
            L(2, 1, 0, 2, 1500, 1900, 3000), L(3, 1, 2, 0, 2020, 2420, 3000),        // F1 HUB-B-HUB
            L(4, 2, 0, 3, 4000, 4300, 2200), L(5, 2, 3, 0, 4420, 4720, 2200),        // F2 HUB-C-HUB
            L(6, 3, 0, 1, 5000, 5300, 2000), L(7, 3, 1, 2, 5420, 5800, 2500),        // F3 HUB-A-B-HUB
            L(8, 3, 2, 0, 5920, 6320, 3000),
            L(9, 4, 0, 3, 7000, 7300, 2200), L(10, 4, 3, 1, 7420, 7800, 2400),       // F4 HUB-C-A-HUB
            L(11, 4, 1, 0, 7920, 8220, 2000),
        };
        Flight F(int id, string code, int[] legIds, bool mand, double cBig, double cSml) => new()
        {
            Id = id, Code = code, LegIds = legIds, IsExternal = false,
            IsMandatory = mand || allMandatory, FixedCostByFleet = [cBig, cSml],
        };
        var flights = new[]
        {
            F(0, "F0", [0, 1], mand: true, 900, 500),
            F(1, "F1", [2, 3], mand: false, 1200, 700),
            F(2, "F2", [4, 5], mand: true, 950, 520),
            F(3, "F3", [6, 7, 8], mand: false, 1500, 900),
            F(4, "F4", [9, 10, 11], mand: false, 1450, 880),
        };
        var ods = new[]
        {
            new Od { Id = 0, Origin = 0, Destination = 1, Avail = 0, MaxDeliveryTime = 3000, Weight = 6, Volume = 36, Rate = 320 },
            new Od { Id = 1, Origin = 1, Destination = 2, Avail = 300, MaxDeliveryTime = 8000, Weight = 3, Volume = 18, Rate = 520 },
            new Od { Id = 2, Origin = 0, Destination = 3, Avail = 2000, MaxDeliveryTime = 4000, Weight = 8, Volume = 48, Rate = 350 },
            new Od { Id = 3, Origin = 3, Destination = 1, Avail = 6000, MaxDeliveryTime = 3000, Weight = 2, Volume = 13, Rate = 410 },
            new Od { Id = 4, Origin = 2, Destination = 0, Avail = 1500, MaxDeliveryTime = 2000, Weight = 4, Volume = 22, Rate = 260 },
        };
        var inst = new Instance
        {
            Name = "small", Period = Period.Weekly,
            Airports = airports, Fleets = fleets, Legs = legs, Flights = flights, Ods = ods,
        };
        inst.Validate();
        return inst;
    }
}
