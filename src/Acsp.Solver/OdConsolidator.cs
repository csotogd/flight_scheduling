using Acsp.Core;

namespace Acsp.Solver;

/// <summary>
/// Screening-model coarsening for very large demand matrices: tiny, far O&amp;Ds (individually
/// irrelevant for network design but collectively meaningful) are consolidated into pseudo
/// O&amp;Ds between their nearest hubs, so their aggregate tonnage still pushes duals and can
/// still justify flights — at a fraction of the row/pricing cost. The design loop runs on the
/// coarse instance; the final solve must run on the ORIGINAL instance, so delivered schedules,
/// flows and profits are always exact (see NetworkDesigner). Toggle: DesignOptions.
/// </summary>
public static class OdConsolidator
{
    public sealed record Report(Instance Coarse, int MembersConsolidated, int PseudoOds,
        double TonnesConsolidated);

    /// <summary>
    /// Consolidates O&amp;Ds with weight &lt;= maxTonnes and distance &gt;= minKm into per
    /// (origin-hub, destination-hub, availability-day) pseudo O&amp;Ds. Members must have a
    /// delivery allowance of at least two days: the pseudo od travels hub-to-hub, so one day
    /// of allowance is reserved for the feeder legs the coarse model does not see.
    /// </summary>
    public static Report Consolidate(Instance inst, double maxTonnes = 1.0, double minKm = 4000)
    {
        var hubs = inst.Airports.Where(a => a.IsTransferHub).ToList();
        if (hubs.Count == 0)
            return new Report(inst, 0, 0, 0);

        double Dist(int a, int b) => GreatCircleKm(inst.Airports[a], inst.Airports[b]);
        var nearestHub = new Dictionary<int, int>();
        int NH(int a)
        {
            if (!nearestHub.TryGetValue(a, out int h))
                nearestHub[a] = h = hubs.OrderBy(x => Dist(a, x.Id)).First().Id;
            return h;
        }

        var kept = new List<Od>();
        var groups = new Dictionary<(int HubO, int HubD, int Day), List<Od>>();
        foreach (var od in inst.Ods)
        {
            bool tinyFar = od.Weight <= maxTonnes
                && Dist(od.Origin, od.Destination) >= minKm
                && od.MaxDeliveryTime >= 2 * 1440;
            int hubO = tinyFar ? NH(od.Origin) : -1, hubD = tinyFar ? NH(od.Destination) : -1;
            if (!tinyFar || hubO == hubD)
            {
                kept.Add(od);
                continue;
            }
            var key = (hubO, hubD, od.Avail / 1440);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(od);
        }

        int members = 0;
        double tonnes = 0;
        var pseudo = new List<Od>();
        foreach (var ((hubO, hubD, _), list) in groups)
        {
            if (list.Count == 1) { kept.Add(list[0]); continue; } // nothing to gain
            double w = list.Sum(o => o.Weight);
            // conservative window: available once ALL members are, deliverable within the
            // tightest member allowance minus one day reserved for the unmodeled feeders
            int avail = list.Max(o => o.Avail);
            // members share a 24h bucket, so max(avail) - each avail <= 1 day
            int del = list.Min(o => o.MaxDeliveryTime) - 1440;
            members += list.Count;
            tonnes += w;
            pseudo.Add(new Od
            {
                Id = -1, Origin = hubO, Destination = hubD, Avail = avail,
                MaxDeliveryTime = del,
                Weight = Math.Round(w, 3),
                Volume = Math.Round(list.Sum(o => o.Volume), 3),
                // tonnage-weighted rate keeps aggregate revenue potential exact
                Rate = Math.Round(list.Sum(o => o.Rate * o.Weight) / w, 2),
            });
        }

        var ods = kept.Concat(pseudo)
            .Select((o, id) => new Od
            {
                Id = id, Origin = o.Origin, Destination = o.Destination, Avail = o.Avail,
                MaxDeliveryTime = o.MaxDeliveryTime, Weight = o.Weight, Volume = o.Volume,
                Rate = o.Rate,
            }).ToArray();
        var coarse = new Instance
        {
            Name = inst.Name, Period = inst.Period, Airports = inst.Airports,
            Fleets = inst.Fleets, Legs = inst.Legs, Flights = inst.Flights, Ods = ods,
        };
        coarse.Validate();
        return new Report(coarse, members, pseudo.Count, Math.Round(tonnes, 1));
    }

    private static double GreatCircleKm(Airport a, Airport b)
    {
        double dLat = (b.Lat - a.Lat) * Math.PI / 180, dLon = (b.Lon - a.Lon) * Math.PI / 180;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(a.Lat * Math.PI / 180) * Math.Cos(b.Lat * Math.PI / 180) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 6371.0 * 2 * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}
