namespace Acsp.Core;

/// <summary>A cargo flow path / itinerary for one O&amp;D (§3.1.8).</summary>
public sealed class CargoPath
{
    public required int OdId { get; init; }
    public required int[] LegIds { get; init; }

    /// <summary>m_p: rate of the O&amp;D minus variable transportation, transfer and storage costs.</summary>
    public double Margin(Instance inst)
    {
        var od = inst.Ods[OdId];
        double cost = 0;
        for (int i = 0; i < LegIds.Length; i++)
        {
            var leg = inst.Legs[LegIds[i]];
            cost += leg.VariableCostPerTonne;
            if (i + 1 < LegIds.Length)
            {
                var next = inst.Legs[LegIds[i + 1]];
                if (leg.FlightId != next.FlightId)
                {
                    var ap = inst.Airports[leg.Destination];
                    cost += ap.TransferCostPerTonne;
                    cost += ap.StorageCostPerTonneHour * inst.Period.Time(leg.Arr, next.Dep) / 60.0;
                }
            }
        }
        return od.Rate - cost;
    }

    /// <summary>Total elapsed minutes from avail_od to DELIVERED (left side of CR-5-DELTIME):
    /// includes cargo handling — the shipment cannot board a flight departing less than
    /// handling minutes after avail (it catches next week's departure instead), and is only
    /// delivered handling minutes after final arrival.</summary>
    public int TotalDeliveryTime(Instance inst)
    {
        var od = inst.Ods[OdId];
        var p = inst.Period;
        int h = inst.CargoHandlingMinutes;
        var first = inst.Legs[LegIds[0]];
        int wait = p.Time(od.Avail, first.Dep);
        if (wait < h) wait += p.N; // loading not finished: only next week's departure works
        int t = wait + first.BlockTime(p);
        for (int i = 1; i < LegIds.Length; i++)
        {
            var prev = inst.Legs[LegIds[i - 1]];
            var leg = inst.Legs[LegIds[i]];
            t += p.Time(prev.Arr, leg.Dep) + leg.BlockTime(p);
        }
        return t + h; // unloading at the destination
    }

    /// <summary>Feasibility per §3.1.8 plus CR-6/CR-7 (transfer hub with adequate transfer time).</summary>
    public bool IsFeasible(Instance inst, out string? reason)
    {
        var od = inst.Ods[OdId];
        var p = inst.Period;
        if (LegIds.Length == 0) { reason = "empty path"; return false; }
        var first = inst.Legs[LegIds[0]];
        var last = inst.Legs[LegIds[^1]];
        if (first.Origin != od.Origin) { reason = "origin mismatch"; return false; }
        if (last.Destination != od.Destination) { reason = "destination mismatch"; return false; }
        for (int i = 0; i + 1 < LegIds.Length; i++)
        {
            var a = inst.Legs[LegIds[i]];
            var b = inst.Legs[LegIds[i + 1]];
            if (a.Destination != b.Origin) { reason = $"legs {a.Id}->{b.Id} not connected"; return false; }
            if (a.FlightId != b.FlightId)
            {
                var ap = inst.Airports[a.Destination];
                if (!ap.IsTransferHub) { reason = $"transfer at non-hub {ap.Code}"; return false; }
                if (p.Time(a.Arr, b.Dep) < ap.MinTransferTime)
                { reason = $"transfer time too short at {ap.Code}"; return false; }
            }
            else
            {
                // consecutive legs of the same flight must actually be consecutive in it
                var flight = inst.Flights[a.FlightId];
                int ia = Array.IndexOf(flight.LegIds, a.Id);
                if (ia < 0 || ia + 1 >= flight.LegIds.Length || flight.LegIds[ia + 1] != b.Id)
                { reason = $"legs {a.Id},{b.Id} not consecutive in flight {flight.Code}"; return false; }
            }
        }
        if (TotalDeliveryTime(inst) > od.MaxDeliveryTime) { reason = "max delivery time exceeded"; return false; }
        reason = null;
        return true;
    }

    public string Key() => $"{OdId}:{string.Join(',', LegIds)}";
}
