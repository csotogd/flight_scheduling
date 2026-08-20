using Acsp.Core;

namespace Acsp.Solver;

/// <summary>
/// Planner-assistant module OUTSIDE the optimization (pragmatic paradigm, §2.1): analyzes the
/// demand that could not be served because no feasible itinerary exists, and proposes candidate
/// OPTIONAL flights (hub round trips timed to the stranded demand). The extended instance is
/// re-optimized afterwards — the model, not the heuristic, decides whether the candidates pay off.
/// </summary>
public static class FlightProposer
{
    public sealed record Proposal(string Code, string[] Route, int DepMinute, string TargetPair,
        double TargetTonnes, string Reason);

    public sealed record Result(Instance Extended, List<Proposal> Proposals,
        int UnservableBefore, int UnservableAfter, double TonnesBefore, double TonnesAfter);

    public static Result Propose(Instance inst, double[] shippedByOd, int maxProposals = 14)
    {
        var pricer = new PathPricer(inst);
        var allowAll = PricingRestrictions.AllowAll(inst);
        var probe = MasterDuals.Zero(inst);
        bool Servable(PathPricer pr, PricingRestrictions rest, Od od)
        {
            probe.OdDemand[od.Id] = -1e9;
            var found = pr.PriceOd(od, probe, rest);
            probe.OdDemand[od.Id] = 0;
            return found is not null;
        }

        // stranded demand: unservable O&Ds grouped by direction
        var stranded = inst.Ods
            .Where(od => od.Weight - (shippedByOd.Length > od.Id ? shippedByOd[od.Id] : 0) > 1e-3)
            .Where(od => !Servable(pricer, allowAll, od))
            .ToList();
        double tonnesBefore = stranded.Sum(o => o.Weight);

        var byPair = stranded
            .GroupBy(od => (od.Origin, od.Destination))
            .OrderByDescending(g => g.Sum(o => o.Weight * o.Rate))
            .ToList();

        var hubs = inst.Airports.Where(a => a.IsTransferHub).ToList();
        double maxRange = inst.Fleets.Max(k => k.RangeKm);
        double CostPerKm(int k)
        {
            var ratios = inst.CargoFlights.Where(f => inst.Compatible(k, f.Id))
                .Select(f => f.FixedCostByFleet[k] / Math.Max(1, f.LegIds.Sum(l => inst.Legs[l].DistanceKm)))
                .OrderBy(x => x).ToList();
            return ratios.Count > 0 ? ratios[ratios.Count / 2] : 4.0;
        }
        var costPerKm = Enumerable.Range(0, inst.Fleets.Length).Select(CostPerKm).ToArray();
        double varPerKm = inst.Legs.Length > 0
            ? inst.Legs.Average(l => l.VariableCostPerTonne / Math.Max(1, l.DistanceKm)) : 0.035;

        double Dist(int a, int b) => HaversineKm(
            inst.Airports[a].Lat, inst.Airports[a].Lon, inst.Airports[b].Lat, inst.Airports[b].Lon);

        var proposals = new List<(Proposal Info, int[] RouteIds, int Dep)>();
        var seen = new HashSet<string>();
        var p = inst.Period;

        void ProposeRoundTrip(int hub, int spoke, int depAtSpokeSide, bool pickup,
            string pair, double tonnes, string reason)
        {
            if (proposals.Count >= maxProposals) return;
            double d = Dist(hub, spoke);
            if (d > maxRange || d < 250) return;
            int block = (int)Math.Round(d / 850.0 * 60) + 40;
            const int ground = 100;
            // pickup: second leg (spoke->hub) departs at depAtSpokeSide;
            // delivery: first leg (hub->spoke) departs so it arrives around depAtSpokeSide
            int depHub = pickup
                ? p.Wrap(depAtSpokeSide - ground - block)
                : p.Wrap(depAtSpokeSide);
            string key = $"{hub}-{spoke}-{depHub / 360}"; // 6h buckets to avoid near-duplicates
            if (!seen.Add(key)) return;
            var code = $"PROP{proposals.Count + 1:D2}";
            proposals.Add((new Proposal(code,
                [inst.Airports[hub].Code, inst.Airports[spoke].Code, inst.Airports[hub].Code],
                depHub, pair, Math.Round(tonnes, 1), reason), [hub, spoke, hub], depHub));
        }

        foreach (var group in byPair)
        {
            if (proposals.Count >= maxProposals) break;
            var (o, d) = group.Key;
            double tonnes = group.Sum(x => x.Weight);
            var od = group.OrderByDescending(x => x.Weight).First();
            string pair = $"{inst.Airports[o].Code}->{inst.Airports[d].Code}";

            // best hub: minimal detour with both legs in range
            var hub = hubs.Where(h => h.Id != o && h.Id != d)
                .Where(h => Dist(o, h.Id) <= maxRange && Dist(h.Id, d) <= maxRange)
                .OrderBy(h => Dist(o, h.Id) + Dist(h.Id, d))
                .FirstOrDefault();

            if (inst.Airports[o].IsTransferHub || inst.Airports[d].IsTransferHub)
            {
                // one endpoint is already a hub: a single round trip connects the pair
                int h = inst.Airports[o].IsTransferHub ? o : d;
                int spoke = h == o ? d : o;
                bool pickup = h == d; // cargo boards at the spoke when the destination is the hub
                int t = pickup ? p.Wrap(od.Avail + 240) : p.Wrap(od.Avail + 240);
                ProposeRoundTrip(h, spoke, t, pickup, pair, tonnes,
                    pickup ? "direct pickup to the hub" : "direct delivery from the hub");
            }
            else if (hub is not null)
            {
                // two coordinated round trips: pick up at o, deliver at d via the hub
                int pickupDep = p.Wrap(od.Avail + 240);
                ProposeRoundTrip(hub.Id, o, pickupDep, pickup: true, pair, tonnes,
                    $"pickup towards hub {hub.Code}");
                int blockOh = (int)Math.Round(Dist(o, hub.Id) / 850.0 * 60) + 40;
                int deliverDep = p.Wrap(pickupDep + blockOh + inst.Airports[hub.Id].MinTransferTime + 120);
                ProposeRoundTrip(hub.Id, d, deliverDep, pickup: false, pair, tonnes,
                    $"delivery from hub {hub.Code}");
            }
        }

        // materialize the extended instance with the proposals as OPTIONAL flights
        var legs = inst.Legs.ToList();
        var flights = inst.Flights.ToList();
        foreach (var (info, route, dep) in proposals)
        {
            int flightId = flights.Count;
            var legIds = new List<int>();
            int t = dep;
            for (int i = 0; i + 1 < route.Length; i++)
            {
                double dist = Dist(route[i], route[i + 1]);
                int block = (int)Math.Round(dist / 850.0 * 60) + 40;
                legs.Add(new Leg
                {
                    Id = legs.Count, FlightId = flightId,
                    Origin = route[i], Destination = route[i + 1],
                    Dep = p.Wrap(t), Arr = p.Wrap(t + block), DistanceKm = dist,
                    VariableCostPerTonne = Math.Round(dist * varPerKm, 2),
                });
                legIds.Add(legs.Count - 1);
                t += block + 100;
            }
            double routeKm = legIds.Sum(l => legs[l].DistanceKm);
            flights.Add(new Flight
            {
                Id = flightId, Code = info.Code, LegIds = [.. legIds],
                IsExternal = false, IsMandatory = false,
                FixedCostByFleet = Enumerable.Range(0, inst.Fleets.Length)
                    .Select(k => Math.Round(routeKm * costPerKm[k] + legIds.Count * 2000, 2)).ToArray(),
            });
        }
        var extended = new Instance
        {
            Name = inst.Name + "+prop", Period = inst.Period,
            Airports = inst.Airports, Fleets = inst.Fleets,
            Legs = [.. legs], Flights = [.. flights], Ods = inst.Ods,
        };
        extended.Validate();

        // how much stranded demand becomes reachable?
        var pricer2 = new PathPricer(extended);
        var allowAll2 = PricingRestrictions.AllowAll(extended);
        var probe2 = MasterDuals.Zero(extended);
        int stillUnservable = 0;
        double tonnesAfter = 0;
        foreach (var od in stranded)
        {
            probe2.OdDemand[od.Id] = -1e9;
            bool ok = pricer2.PriceOd(extended.Ods[od.Id], probe2, allowAll2) is not null;
            probe2.OdDemand[od.Id] = 0;
            if (!ok) { stillUnservable++; tonnesAfter += od.Weight; }
        }

        return new Result(extended, proposals.Select(x => x.Info).ToList(),
            stranded.Count, stillUnservable, Math.Round(tonnesBefore, 1), Math.Round(tonnesAfter, 1));
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        double dLat = (lat2 - lat1) * Math.PI / 180, dLon = (lon2 - lon1) * Math.PI / 180;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}
