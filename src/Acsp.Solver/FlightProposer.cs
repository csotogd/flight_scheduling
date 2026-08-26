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
    /// <param name="Key">Dedup key (route + 6h departure bucket); the design loop uses it to
    /// block re-proposal while the candidate is alive or freshly evicted (amnesty).</param>
    public sealed record Proposal(string Code, string[] Route, int DepMinute, string TargetPair,
        double TargetTonnes, string Reason, string Key = "");

    public sealed record Result(Instance Extended, List<Proposal> Proposals,
        int UnservableBefore, int UnservableAfter, double TonnesBefore, double TonnesAfter,
        HashSet<string> ProposalKeys);

    /// <param name="maxProposals">Upper bound on generated candidates (ranked by revenue at risk).</param>
    /// <param name="codePrefix">Flight code prefix, e.g. "P3-" for design round 3.</param>
    /// <param name="excludeKeys">Dedup keys from earlier rounds; new keys are added to the result.</param>
    /// <param name="includeCapacityTargets">Also target demand that has a route but was left
    /// unserved (capacity crowded out), not only unroutable demand.</param>
    /// <param name="includeDirect">Also propose direct point-to-point rotations (no hub) for
    /// pairs whose unserved demand fills at least half an airplane; lower priority than hub
    /// round trips (appended after them).</param>
    /// <param name="includeExternalFallback">For pairs no own-fleet proposal can reach, offer
    /// bookable EXTERNAL capacity (charter/interline) at a markedly higher cost; the model
    /// only books it when the demand still pays at that price.</param>
    /// <param name="includeTrunks">Also propose inter-hub trunk shuttles aimed at the
    /// cross-hub unserved tonnage (od endpoints assigned to their nearest hubs).</param>
    /// <param name="zoneFilter">When set, hub/direct/external targets are restricted to ods
    /// touching the zone (origin or destination accepted by the filter); trunks stay global
    /// by design — they serve every zone at once.</param>
    /// <param name="includeWaves">Also propose hub waves: coordinated feeder + trunk +
    /// distribution bundles per (origin-hub, destination-hub, day) corridor, aimed at the
    /// long tail of tiny ods no single flight can monetize — each od needs the whole chain
    /// inside its delivery window, so the pieces only pay together and are proposed
    /// together (the local-branching ball adopts or rejects the set at once).</param>
    public static Result Propose(Instance inst, double[] shippedByOd, int maxProposals = 14,
        string codePrefix = "PROP", ISet<string>? excludeKeys = null,
        bool includeCapacityTargets = false, bool includeDirect = false,
        bool includeExternalFallback = false, bool includeTrunks = false,
        Func<int, bool>? zoneFilter = null, bool includeWaves = false)
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

        // demand at risk: O&Ds with unserved tonnes, split into unroutable (no feasible
        // itinerary at all) and, optionally, crowded-out (routable but left on the ground)
        double Unshipped(Od od) => od.Weight - (shippedByOd.Length > od.Id ? shippedByOd[od.Id] : 0);
        var unserved = inst.Ods.Where(od => Unshipped(od) > 1e-3).ToList();
        var stranded = unserved.Where(od => !Servable(pricer, allowAll, od)).ToList();
        double tonnesBefore = stranded.Sum(o => o.Weight);
        var strandedSet = new HashSet<int>(stranded.Select(o => o.Id));

        var byPair = (includeCapacityTargets ? unserved : stranded)
            .Where(od => zoneFilter is null || zoneFilter(od.Origin) || zoneFilter(od.Destination))
            .GroupBy(od => (od.Origin, od.Destination))
            .OrderByDescending(g => g.Sum(o => Unshipped(o) * o.Rate))
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

        var proposals = new List<(Proposal Info, int[] RouteIds, int Dep, bool External)>();
        var seen = excludeKeys is null ? [] : new HashSet<string>(excludeKeys);
        var newKeys = new HashSet<string>();
        var coveredPairs = new HashSet<(int, int)>();
        var p = inst.Period;

        // batch quotas so lower-priority kinds are never starved on dense instances
        // (~40% hub / 15% direct / 10% trunk / 30% wave / 5% external when everything is
        // enabled); unused quota flows down to the next pass
        int externalQuota = includeExternalFallback ? Math.Max(1, maxProposals * 5 / 100) : 0;
        int waveQuota = includeWaves ? Math.Max(1, maxProposals * 30 / 100) : 0;
        int trunkQuota = includeTrunks ? Math.Max(1, maxProposals * 10 / 100) : 0;
        int directQuota = includeDirect ? Math.Max(1, maxProposals * 15 / 100) : 0;
        int hubCap = maxProposals - directQuota - trunkQuota - waveQuota - externalQuota;
        int directCap = maxProposals - trunkQuota - waveQuota - externalQuota;
        int trunkCap = maxProposals - waveQuota - externalQuota;
        int waveCap = maxProposals - externalQuota;
        int cap = hubCap;

        // nearest-hub assignment shared by the trunk and wave passes
        var nearestHub = new Dictionary<int, int>();
        int NH(int a)
        {
            if (!nearestHub.TryGetValue(a, out int h))
                nearestHub[a] = h = hubs.OrderBy(x => Dist(a, x.Id)).First().Id;
            return h;
        }

        void ProposeRoundTrip(int hub, int spoke, int depAtSpokeSide, bool pickup,
            string pair, double tonnes, string reason)
        {
            if (proposals.Count >= cap) return;
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
            newKeys.Add(key);
            var code = $"{codePrefix}{proposals.Count + 1:D2}";
            proposals.Add((new Proposal(code,
                [inst.Airports[hub].Code, inst.Airports[spoke].Code, inst.Airports[hub].Code],
                depHub, pair, Math.Round(tonnes, 1), reason, key), [hub, spoke, hub], depHub, false));
        }

        foreach (var group in byPair)
        {
            if (proposals.Count >= cap) break;
            var (o, d) = group.Key;
            int countBefore = proposals.Count;
            string pair = $"{inst.Airports[o].Code}->{inst.Airports[d].Code}";
            bool routable = !strandedSet.Contains(group.First().Id);
            string why(string what) => routable ? $"{what} (capacity crowded out)" : what;

            // best hub: minimal detour with both legs in range
            var hub = hubs.Where(h => h.Id != o && h.Id != d)
                .Where(h => Dist(o, h.Id) <= maxRange && Dist(h.Id, d) <= maxRange)
                .OrderBy(h => Dist(o, h.Id) + Dist(h.Id, d))
                .FirstOrDefault();

            // one proposal per distinct 6h availability window, heaviest first: a large batch
            // covers the same pair at several times of the week instead of just once
            var windows = group.GroupBy(x => x.Avail / 360)
                .OrderByDescending(g => g.Sum(x => Unshipped(x)))
                .Select(g => (Avail: g.First().Avail, Tonnes: g.Sum(x => Unshipped(x))));
            foreach (var (avail, tonnes) in windows)
            {
                if (proposals.Count >= maxProposals) break;
                if (inst.Airports[o].IsTransferHub || inst.Airports[d].IsTransferHub)
                {
                    // one endpoint is already a hub: a single round trip connects the pair
                    int h = inst.Airports[o].IsTransferHub ? o : d;
                    int spoke = h == o ? d : o;
                    bool pickup = h == d; // cargo boards at the spoke when the destination is the hub
                    int t = p.Wrap(avail + 240);
                    ProposeRoundTrip(h, spoke, t, pickup, pair, tonnes,
                        why(pickup ? "direct pickup to the hub" : "direct delivery from the hub"));
                }
                else if (hub is not null)
                {
                    // two coordinated round trips: pick up at o, deliver at d via the hub
                    int pickupDep = p.Wrap(avail + 240);
                    ProposeRoundTrip(hub.Id, o, pickupDep, pickup: true, pair, tonnes,
                        why($"pickup towards hub {hub.Code}"));
                    int blockOh = (int)Math.Round(Dist(o, hub.Id) / 850.0 * 60) + 40;
                    int deliverDep = p.Wrap(pickupDep + blockOh + inst.Airports[hub.Id].MinTransferTime + 120);
                    ProposeRoundTrip(hub.Id, d, deliverDep, pickup: false, pair, tonnes,
                        why($"delivery from hub {hub.Code}"));
                }
            }
            if (proposals.Count > countBefore) coveredPairs.Add((o, d));
        }

        // pass 2 (lower priority): direct point-to-point rotations sampled WITHOUT replacement
        // with probability proportional to proximity x the pair's share of its origin's
        // unserved volume - close, locally dominant flows are the ones a direct can win
        if (includeDirect)
        {
            cap = directCap;
            // deterministic rng so a (codePrefix, instance) pair reproduces its batch
            var rng = new Random(codePrefix.Aggregate(17, (h, c) => h * 31 + c));
            var originTotal = new Dictionary<int, double>();
            foreach (var g in byPair)
                originTotal[g.Key.Origin] =
                    originTotal.GetValueOrDefault(g.Key.Origin) + g.Sum(x => Unshipped(x));
            var sampled = byPair
                .Select(g =>
                {
                    var (o, d) = g.Key;
                    double dist = Dist(o, d);
                    double tonnes = g.Sum(x => Unshipped(x));
                    double share = tonnes / Math.Max(1e-9, originTotal[o]);
                    double w = Math.Exp(-dist / 4000.0) * share;
                    // Efraimidis-Spirakis weighted sampling without replacement
                    return (Group: g, Dist: dist, Tonnes: tonnes,
                        SampleKey: Math.Pow(rng.NextDouble(), 1.0 / Math.Max(1e-9, w)));
                })
                .Where(x => x.Dist is >= 250 && x.Dist <= maxRange)
                .OrderByDescending(x => x.SampleKey);
            foreach (var (group, dist, tonnes, _) in sampled)
            {
                if (proposals.Count >= cap) break;
                var (o, d) = group.Key;
                bool routable = !strandedSet.Contains(group.First().Id);
                var od = group.OrderByDescending(x => Unshipped(x)).First();
                int dep = p.Wrap(od.Avail + 240);
                string key = $"d{o}-{d}-{dep / 360}";
                if (!seen.Add(key)) continue;
                newKeys.Add(key);
                var code = $"{codePrefix}{proposals.Count + 1:D2}";
                string pair = $"{inst.Airports[o].Code}->{inst.Airports[d].Code}";
                proposals.Add((new Proposal(code,
                    [inst.Airports[o].Code, inst.Airports[d].Code, inst.Airports[o].Code],
                    dep, pair, Math.Round(tonnes, 1),
                    $"direct rotation, no hub ({(routable ? "capacity crowded out" : "no route")}; " +
                    $"p ∝ proximity × od share of origin)", key), [o, d, o], dep, false));
                coveredPairs.Add((o, d));
            }
        }

        // pass 2b: inter-hub trunk shuttles - assign every unserved od to its nearest hubs
        // and aim scheduled hub<->hub capacity at the heaviest cross-hub corridors, which
        // feeds all multi-hub itineraries at once
        if (includeTrunks && hubs.Count >= 2)
        {
            cap = trunkCap;
            var corridors = new Dictionary<(int, int), (double Tonnes, double BestW, int Avail)>();
            foreach (var od in (includeCapacityTargets ? unserved : stranded))
            {
                int h1 = NH(od.Origin), h2 = NH(od.Destination);
                if (h1 == h2) continue;
                double w = Unshipped(od);
                var cur = corridors.GetValueOrDefault((h1, h2), (0, -1, 0));
                corridors[(h1, h2)] = (cur.Tonnes + w, Math.Max(cur.BestW, w),
                    w > cur.BestW ? od.Avail : cur.Avail);
            }
            foreach (var ((h1, h2), (tonnes, _, avail)) in corridors.OrderByDescending(kv => kv.Value.Tonnes))
            {
                if (proposals.Count >= cap) break;
                if (Dist(h1, h2) > maxRange || Dist(h1, h2) < 250) continue;
                int dep = p.Wrap(avail + 240);
                string key = $"t{h1}-{h2}-{dep / 360}";
                if (!seen.Add(key)) continue;
                newKeys.Add(key);
                var code = $"{codePrefix}{proposals.Count + 1:D2}";
                string pair = $"{inst.Airports[h1].Code}->{inst.Airports[h2].Code}";
                proposals.Add((new Proposal(code,
                    [inst.Airports[h1].Code, inst.Airports[h2].Code, inst.Airports[h1].Code],
                    dep, pair, Math.Round(tonnes, 1),
                    $"interhub trunk {inst.Airports[h1].Code}↔{inst.Airports[h2].Code} " +
                    $"({tonnes:F0}t cross-hub demand)", key), [h1, h2, h1], dep, false));
            }
        }

        // pass 2c: hub waves - the long tail of tiny ods (a few tonnes each, scattered over
        // many spokes) cannot be monetized by any single flight: each od needs a complete
        // feeder -> (trunk) -> distribution chain inside its delivery window, so the pieces
        // only pay together. A wave proposes the whole mechanism at once for the heaviest
        // (origin-hub, destination-hub, day) corridors: up to 3 feeder round trips arriving
        // before the bank closes, the trunk departing at the close, and up to 3 distribution
        // round trips after the destination sort. Target tonnage counts ONLY the ods whose
        // windows actually fit the chain.
        if (includeWaves && hubs.Count >= 1)
        {
            cap = waveCap;
            int Block(int a, int b) => (int)Math.Round(Dist(a, b) / 850.0 * 60) + 40;
            bool InRange(int a, int b) { double d0 = Dist(a, b); return d0 is >= 250 && d0 <= maxRange; }
            // economic floor: a wave must aim at least a quarter of the smallest airplane
            double waveMin = 0.25 * inst.Fleets.Min(k => k.MaxWeight);

            var banks = new Dictionary<(int H1, int H2, int Day), List<Od>>();
            foreach (var od in (includeCapacityTargets ? unserved : stranded))
            {
                if (zoneFilter is not null && !(zoneFilter(od.Origin) || zoneFilter(od.Destination)))
                    continue;
                var key = (NH(od.Origin), NH(od.Destination), od.Avail / 1440);
                if (!banks.TryGetValue(key, out var list)) banks[key] = list = [];
                list.Add(od);
            }
            foreach (var ((h1, h2, day), members) in banks
                .OrderByDescending(kv => kv.Value.Sum(Unshipped)))
            {
                if (proposals.Count >= cap) break;
                if (members.Sum(Unshipped) < waveMin) break; // sorted: only lighter banks follow
                if (h1 != h2 && !InRange(h1, h2)) continue;

                // heaviest feeder and distribution spokes (per-spoke departure = latest
                // member availability + loading, so every bucket member makes its feeder)
                var feeders = members.Where(o => o.Origin != h1 && InRange(o.Origin, h1))
                    .GroupBy(o => o.Origin)
                    .Select(g => (Spoke: g.Key, T: g.Sum(Unshipped),
                        DepSpoke: g.Max(o => o.Avail) + 60))
                    .OrderByDescending(x => x.T).Take(3).ToList();
                var dists = members.Where(o => o.Destination != h2 && InRange(h2, o.Destination))
                    .GroupBy(o => o.Destination)
                    .Select(g => (Spoke: g.Key, T: g.Sum(Unshipped)))
                    .OrderByDescending(x => x.T).Take(3).ToList();

                // bank chain: feeders land, sort, trunk, sort, distribute (times unwrapped;
                // materialization wraps). Sort time follows the hub's own transfer minimum.
                int sortH1 = inst.Airports[h1].MinTransferTime + 30;
                int sortH2 = inst.Airports[h2].MinTransferTime + 30;
                var atH1 = members.Where(o => o.Origin == h1).ToList();
                int t1 = Math.Max(
                    feeders.Count > 0 ? feeders.Max(x => x.DepSpoke + Block(x.Spoke, h1)) + sortH1 : 0,
                    atH1.Count > 0 ? atH1.Max(o => o.Avail) + 60 : 0);
                if (t1 == 0) continue; // nothing can reach the bank
                int trunkArr = h1 == h2 ? t1 : t1 + Block(h1, h2);
                int t2 = h1 == h2 ? t1 : trunkArr + sortH2;

                // honest wave tonnage: origin must reach the bank, destination arrival must
                // beat the od's delivery window
                var feedSet = feeders.Select(x => x.Spoke).ToHashSet();
                var distSet = dists.Select(x => x.Spoke).ToHashSet();
                double served = 0;
                foreach (var od in members)
                {
                    bool originOk = od.Origin == h1 || feedSet.Contains(od.Origin);
                    if (!originOk) continue;
                    int arrive = od.Destination == h2 ? trunkArr : t2 + Block(h2, od.Destination);
                    if (distSet.Contains(od.Destination) || od.Destination == h2)
                        if (arrive <= od.Avail + od.MaxDeliveryTime)
                            served += Unshipped(od);
                }
                if (served < waveMin) continue;

                string corridor = $"{inst.Airports[h1].Code}->{inst.Airports[h2].Code}";
                string tag = $"hub wave {inst.Airports[h1].Code}→{inst.Airports[h2].Code} " +
                    $"d{day} ({served:F0}t bank)";
                foreach (var f in feeders)
                    ProposeRoundTrip(h1, f.Spoke, p.Wrap(f.DepSpoke), pickup: true,
                        corridor, served, $"{tag}: feeder");
                if (h1 != h2 && proposals.Count < cap)
                {
                    string tkey = $"t{h1}-{h2}-{p.Wrap(t1) / 360}";
                    if (seen.Add(tkey))
                    {
                        newKeys.Add(tkey);
                        var code = $"{codePrefix}{proposals.Count + 1:D2}";
                        proposals.Add((new Proposal(code,
                            [inst.Airports[h1].Code, inst.Airports[h2].Code, inst.Airports[h1].Code],
                            p.Wrap(t1), corridor, Math.Round(served, 1),
                            $"{tag}: trunk", tkey), [h1, h2, h1], p.Wrap(t1), false));
                    }
                }
                foreach (var x in dists)
                    ProposeRoundTrip(h2, x.Spoke, p.Wrap(t2), pickup: false,
                        corridor, served, $"{tag}: distribution");
            }
        }

        // pass 3 (last resort): bookable external capacity at a clear cost premium for pairs
        // no own-fleet proposal reaches (out of range, no usable hub)
        if (includeExternalFallback)
        {
            cap = maxProposals;
            foreach (var group in byPair)
            {
                if (proposals.Count >= cap) break;
                var (o, d) = group.Key;
                if (coveredPairs.Contains((o, d))) continue;
                if (!strandedSet.Contains(group.First().Id)) continue; // only unroutable demand
                var od = group.OrderByDescending(x => Unshipped(x)).First();
                double tonnes = group.Sum(x => Unshipped(x));
                int dep = p.Wrap(od.Avail + 240);
                string key = $"x{o}-{d}-{dep / 360}";
                if (!seen.Add(key)) continue;
                newKeys.Add(key);
                var code = $"{codePrefix}{proposals.Count + 1:D2}";
                string pair = $"{inst.Airports[o].Code}->{inst.Airports[d].Code}";
                proposals.Add((new Proposal(code,
                    [inst.Airports[o].Code, inst.Airports[d].Code],
                    dep, pair, Math.Round(tonnes, 1),
                    "external charter/interline (premium cost, no own aircraft)", key),
                    [o, d], dep, true));
                coveredPairs.Add((o, d));
            }
        }

        // materialize the extended instance: own proposals as OPTIONAL flights, external
        // fallbacks as bookable EXTERNAL flights at roughly twice the own-fleet economics
        var legs = inst.Legs.ToList();
        var flights = inst.Flights.ToList();
        double externalCapT = inst.Fleets.Max(k => k.MaxWeight);
        double avgCostPerKm = costPerKm.Length > 0 ? costPerKm.Average() : 4.0;
        foreach (var (info, route, dep, external) in proposals)
        {
            int flightId = flights.Count;
            var legIds = new List<int>();
            int t = dep;
            for (int i = 0; i + 1 < route.Length; i++)
            {
                double dist = Dist(route[i], route[i + 1]);
                int block = (int)Math.Round(dist / 850.0 * 60) + 40;
                double varT = external
                    ? Math.Round(2 * (dist * varPerKm + dist * avgCostPerKm / externalCapT), 2)
                    : Math.Round(dist * varPerKm, 2);
                legs.Add(new Leg
                {
                    Id = legs.Count, FlightId = flightId,
                    Origin = route[i], Destination = route[i + 1],
                    Dep = p.Wrap(t), Arr = p.Wrap(t + block), DistanceKm = dist,
                    VariableCostPerTonne = varT,
                    MaxWeight = external ? externalCapT : 0,
                    MaxVolume = external ? externalCapT * 6 : 0,
                });
                legIds.Add(legs.Count - 1);
                t += block + 100;
            }
            double routeKm = legIds.Sum(l => legs[l].DistanceKm);
            flights.Add(new Flight
            {
                Id = flightId, Code = info.Code, LegIds = [.. legIds],
                IsExternal = external, IsMandatory = false,
                FixedCostByFleet = external ? [] : Enumerable.Range(0, inst.Fleets.Length)
                    .Select(k => Math.Round(routeKm * costPerKm[k] + legIds.Count * 2000, 2)).ToArray(),
                ExternalFixedCost = external ? Math.Round(0.25 * routeKm * avgCostPerKm, 2) : 0,
            });
        }
        var extended = new Instance
        {
            Name = inst.Name.EndsWith("+prop") ? inst.Name : inst.Name + "+prop",
            Period = inst.Period, DeliverAll = inst.DeliverAll,
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
            stranded.Count, stillUnservable, Math.Round(tonnesBefore, 1), Math.Round(tonnesAfter, 1),
            newKeys);
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
