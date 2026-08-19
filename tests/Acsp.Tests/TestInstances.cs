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
}
