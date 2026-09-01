using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Acsp.Core;

namespace Acsp.Data;

/// <summary>
/// Excel (.xlsx) import/export of a complete instance, designed for the planner workflow:
/// export the template, edit it in Excel (add/remove flights, change times, adjust demand),
/// upload it back. Build() and Read() are exact round-trip counterparts. Reading returns a
/// per-row validation report; errors block the import, warnings do not.
/// </summary>
public static class InstanceXlsx
{
    public sealed record Message(string Severity, string Sheet, int Row, string Text);
    public sealed record ReadResult(Instance? Instance, List<Message> Messages)
    {
        public bool Ok => Instance is not null;
        public IEnumerable<Message> Errors => Messages.Where(m => m.Severity == "error");
    }

    private static readonly string[] Days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    // ---------------------------------------------------------------- export

    public static byte[] Build(Instance inst)
    {
        var airports = new List<object?[]>
        {
            new object?[] { "Code", "Name", "Lat", "Lon", "Hub", "MinTransferMin",
                "TransferCostPerT", "StorageCostPerTHour", "MaintenanceHubFor",
                "CurfewStart", "CurfewEnd" },
        };
        foreach (var a in inst.Airports)
            airports.Add([a.Code, a.Name, a.Lat, a.Lon, a.IsTransferHub ? "yes" : "",
                a.IsTransferHub ? a.MinTransferTime : null,
                a.IsTransferHub ? a.TransferCostPerTonne : null,
                a.IsTransferHub ? a.StorageCostPerTonneHour : null,
                string.Join(",", inst.Fleets.Where(k => a.MaintenanceHubFor.Length > k.Id
                    && a.MaintenanceHubFor[k.Id]).Select(k => k.Code)),
                a.CurfewStart < 0 ? null : $"{a.CurfewStart / 60:D2}:{a.CurfewStart % 60:D2}",
                a.CurfewEnd < 0 ? null : $"{a.CurfewEnd / 60:D2}:{a.CurfewEnd % 60:D2}"]);

        var fleets = new List<object?[]>
        {
            new object?[] { "Code", "Count", "MaxWeightT", "MaxVolumeM3", "RangeKm", "SpeedKmH",
                "FixedCostPerAircraft", "DefaultGroundMin", "MntMaxCycles", "MntMaxFlightH",
                "MntMaxElapsedH", "MntStopH", "RangeMaxKm", "PayloadAtMaxRangeT" },
        };
        foreach (var k in inst.Fleets)
            fleets.Add([k.Code, k.Count, k.MaxWeight, k.MaxVolume, k.RangeKm, k.CruiseSpeedKmH,
                k.FixedCostPerAircraft, k.DefaultMinGroundTime,
                k.MaxCyclesBetweenMaintenance == int.MaxValue ? null : k.MaxCyclesBetweenMaintenance,
                k.MaxFlightMinutesBetweenMaintenance == int.MaxValue ? null
                    : Math.Round(k.MaxFlightMinutesBetweenMaintenance / 60.0, 2),
                k.MaxElapsedMinutesBetweenMaintenance == int.MaxValue ? null
                    : Math.Round(k.MaxElapsedMinutesBetweenMaintenance / 60.0, 2),
                Math.Round(k.MaintenanceDuration / 60.0, 2),
                k.RangeMaxKm > 0 ? k.RangeMaxKm : null,
                k.PayloadAtMaxRangeT > 0 ? k.PayloadAtMaxRangeT : null]);

        var flights = new List<object?[]>();
        var header = new List<object?> { "FlightCode", "Kind", "LegNo", "From", "To", "Dep", "Arr",
            "DistanceKm", "VarCostPerT", "CapT", "CapVolM3", "ExtFixedCost" };
        header.AddRange(inst.Fleets.Select(k => (object?)$"Fixed:{k.Code}"));
        flights.Add([.. header]);
        foreach (var f in inst.Flights)
            for (int i = 0; i < f.LegIds.Length; i++)
            {
                var l = inst.Legs[f.LegIds[i]];
                var row = new List<object?>
                {
                    f.Code,
                    f.IsExternal ? "external" : f.IsMandatory ? "mandatory" : "optional",
                    i + 1,
                    inst.Airports[l.Origin].Code, inst.Airports[l.Destination].Code,
                    TimeStr(l.Dep), TimeStr(l.Arr),
                    Math.Round(l.DistanceKm, 1), l.VariableCostPerTonne,
                    f.IsExternal ? l.MaxWeight : null,
                    f.IsExternal ? l.MaxVolume : null,
                    f.IsExternal && i == 0 && f.ExternalFixedCost != 0 ? f.ExternalFixedCost : null,
                };
                foreach (var k in inst.Fleets)
                    row.Add(!f.IsExternal && i == 0 && f.FixedCostByFleet.Length > k.Id
                        ? f.FixedCostByFleet[k.Id] : null);
                flights.Add([.. row]);
            }

        var ods = new List<object?[]>
        {
            new object?[] { "From", "To", "Avail", "MaxDeliveryH", "WeightT", "VolumeM3", "RatePerT" },
        };
        foreach (var o in inst.Ods)
            ods.Add([inst.Airports[o.Origin].Code, inst.Airports[o.Destination].Code,
                TimeStr(o.Avail), Math.Round(o.MaxDeliveryTime / 60.0, 2),
                o.Weight, o.Volume, o.Rate]);

        var readme = new List<object?[]>
        {
            new object?[] { "ACSP instance workbook — edit and upload it back" },
            new object?[] { "Times use 'Day HH:MM' with days Mon..Sun (weekly periodic schedule)." },
            new object?[] { "Flights: one row per leg, grouped by FlightCode with LegNo 1,2,..." },
            new object?[] { "Kind: mandatory | optional | external. Optional flights are the" },
            new object?[] { "candidates the optimizer may or may not fly - add yours freely." },
            new object?[] { "Blank DistanceKm = great-circle distance from airport coordinates." },
            new object?[] { "Blank VarCostPerT = estimated from distance. Blank Fixed:<fleet> = estimated." },
            new object?[] { "External flights need CapT (and optionally CapVolM3, ExtFixedCost)." },
            new object?[] { "Blank Mnt* on a fleet = no maintenance constraint." },
            new object?[] { "Airports CurfewStart/End (HH:MM): no arrivals in that window; blank = open." },
            new object?[] { "Fleets RangeMaxKm/PayloadAtMaxRangeT: payload-range curve; blank = single range." },
        };

        var settings = new List<object?[]>
        {
            new object?[] { "Key", "Value" },
            new object?[] { "DeliverAll", inst.DeliverAll ? "yes" : "no" },
            new object?[] { "CargoHandlingMinutes", inst.CargoHandlingMinutes },
        };

        return XlsxWriter.Build(("Readme", readme), ("Settings", settings),
            ("Airports", airports), ("Fleets", fleets), ("Flights", flights), ("ODs", ods));
    }

    private static string TimeStr(int t) =>
        $"{Days[t / 1440 % 7]} {t % 1440 / 60:D2}:{t % 60:D2}";

    // ---------------------------------------------------------------- import

    public static ReadResult Read(byte[] xlsx, string name)
    {
        var msgs = new List<Message>();
        Dictionary<string, List<(int Row, List<string> Cells)>> sheets;
        try { sheets = ParseWorkbook(xlsx); }
        catch (Exception ex)
        {
            msgs.Add(new("error", "-", 0, $"Cannot read the workbook: {ex.Message}"));
            return new ReadResult(null, msgs);
        }

        void Err(string sheet, int row, string text) => msgs.Add(new("error", sheet, row, text));
        void Warn(string sheet, int row, string text) => msgs.Add(new("warning", sheet, row, text));

        List<(int Row, List<string> Cells)> Sheet(string n)
        {
            if (sheets.TryGetValue(n, out var s)) return s;
            Err(n, 0, $"Sheet '{n}' is missing");
            return [];
        }

        var apRows = Sheet("Airports");
        var fleetRows = Sheet("Fleets");
        var flightRows = Sheet("Flights");
        var odRows = Sheet("ODs");
        if (msgs.Any(m => m.Severity == "error")) return new ReadResult(null, msgs);

        // header maps: column name -> index (case-insensitive)
        Dictionary<string, int> Header(List<(int Row, List<string> Cells)> rows) =>
            rows.Count == 0 ? [] : rows[0].Cells
                .Select((c, i) => (c: c.Trim(), i))
                .Where(x => x.c.Length > 0)
                .ToDictionary(x => x.c, x => x.i, StringComparer.OrdinalIgnoreCase);

        string Cell(List<string> cells, Dictionary<string, int> h, string col) =>
            h.TryGetValue(col, out int i) && i < cells.Count ? cells[i].Trim() : "";

        double? Num(string s) => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture,
            out double v) ? v : null;

        // ---- airports
        var apH = Header(apRows);
        foreach (var col in new[] { "Code", "Lat", "Lon" })
            if (!apH.ContainsKey(col)) Err("Airports", 1, $"Missing column '{col}'");
        var airports = new List<Airport>();
        var apIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var mntHubCodes = new List<string[]>();
        foreach (var (row, cells) in apRows.Skip(1))
        {
            string code = Cell(cells, apH, "Code").ToUpperInvariant();
            if (code.Length == 0) continue;
            if (apIdx.ContainsKey(code)) { Err("Airports", row, $"Duplicate airport '{code}'"); continue; }
            double? lat = Num(Cell(cells, apH, "Lat")), lon = Num(Cell(cells, apH, "Lon"));
            if (lat is null || lon is null)
            { Err("Airports", row, $"{code}: Lat/Lon must be numeric"); continue; }
            bool hub = IsYes(Cell(cells, apH, "Hub"));
            // curfew accepts 'HH:MM' or plain minutes-of-day; blank = open all night
            int CurfewMin(string col)
            {
                var s = Cell(cells, apH, col);
                if (s.Length == 0) return -1;
                var parts = s.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out int hh)
                    && int.TryParse(parts[1], out int mm))
                    return hh * 60 + mm;
                return (int)(Num(s) ?? -1);
            }
            airports.Add(new Airport
            {
                Id = airports.Count, Code = code, Name = Cell(cells, apH, "Name"),
                Lat = lat.Value, Lon = lon.Value, IsTransferHub = hub,
                MinTransferTime = (int)(Num(Cell(cells, apH, "MinTransferMin")) ?? 120),
                TransferCostPerTonne = Num(Cell(cells, apH, "TransferCostPerT")) ?? 0,
                StorageCostPerTonneHour = Num(Cell(cells, apH, "StorageCostPerTHour")) ?? 0,
                CurfewStart = CurfewMin("CurfewStart"), CurfewEnd = CurfewMin("CurfewEnd"),
            });
            apIdx[code] = airports.Count - 1;
            mntHubCodes.Add(Cell(cells, apH, "MaintenanceHubFor")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        if (airports.Count < 2) Err("Airports", 0, "Need at least two airports");

        // ---- fleets
        var flH = Header(fleetRows);
        foreach (var col in new[] { "Code", "Count", "MaxWeightT", "RangeKm" })
            if (!flH.ContainsKey(col)) Err("Fleets", 1, $"Missing column '{col}'");
        var fleets = new List<FleetType>();
        var fleetIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (row, cells) in fleetRows.Skip(1))
        {
            string code = Cell(cells, flH, "Code");
            if (code.Length == 0) continue;
            if (fleetIdx.ContainsKey(code)) { Err("Fleets", row, $"Duplicate fleet '{code}'"); continue; }
            double? cnt = Num(Cell(cells, flH, "Count")), w = Num(Cell(cells, flH, "MaxWeightT")),
                range = Num(Cell(cells, flH, "RangeKm"));
            if (cnt is null or <= 0 || w is null or <= 0 || range is null or <= 0)
            { Err("Fleets", row, $"{code}: Count, MaxWeightT and RangeKm must be positive numbers"); continue; }
            double? mntFlightH = Num(Cell(cells, flH, "MntMaxFlightH"));
            double? mntElapsedH = Num(Cell(cells, flH, "MntMaxElapsedH"));
            double? mntCycles = Num(Cell(cells, flH, "MntMaxCycles"));
            fleets.Add(new FleetType
            {
                Id = fleets.Count, Code = code, Count = (int)cnt.Value,
                MaxWeight = w.Value,
                MaxVolume = Num(Cell(cells, flH, "MaxVolumeM3")) ?? w.Value * 6,
                RangeKm = range.Value,
                CruiseSpeedKmH = Num(Cell(cells, flH, "SpeedKmH")) ?? 850,
                FixedCostPerAircraft = Num(Cell(cells, flH, "FixedCostPerAircraft")) ?? 0,
                DefaultMinGroundTime = (int)(Num(Cell(cells, flH, "DefaultGroundMin")) ?? 60),
                MaxCyclesBetweenMaintenance = mntCycles is null ? int.MaxValue : (int)mntCycles.Value,
                MaxFlightMinutesBetweenMaintenance =
                    mntFlightH is null ? int.MaxValue : (int)(mntFlightH.Value * 60),
                MaxElapsedMinutesBetweenMaintenance =
                    mntElapsedH is null ? int.MaxValue : (int)(mntElapsedH.Value * 60),
                MaintenanceDuration = (int)((Num(Cell(cells, flH, "MntStopH")) ?? 8) * 60),
                RangeMaxKm = Num(Cell(cells, flH, "RangeMaxKm")) ?? 0,
                PayloadAtMaxRangeT = Num(Cell(cells, flH, "PayloadAtMaxRangeT")) ?? 0,
            });
            fleetIdx[code] = fleets.Count - 1;
        }
        if (fleets.Count == 0) Err("Fleets", 0, "Need at least one fleet");

        // resolve maintenance hubs now that fleets are known
        for (int a = 0; a < airports.Count; a++)
        {
            if (mntHubCodes[a].Length == 0) continue;
            var mask = new bool[fleets.Count];
            var cost = new double[fleets.Count];
            foreach (var fc in mntHubCodes[a])
            {
                if (fleetIdx.TryGetValue(fc, out int k)) { mask[k] = true; cost[k] = 10000; }
                else Warn("Airports", 0, $"{airports[a].Code}: unknown fleet '{fc}' in MaintenanceHubFor");
            }
            airports[a] = new Airport
            {
                Id = a, Code = airports[a].Code, Name = airports[a].Name,
                Lat = airports[a].Lat, Lon = airports[a].Lon,
                IsTransferHub = airports[a].IsTransferHub,
                MinTransferTime = airports[a].MinTransferTime,
                TransferCostPerTonne = airports[a].TransferCostPerTonne,
                StorageCostPerTonneHour = airports[a].StorageCostPerTonneHour,
                CurfewStart = airports[a].CurfewStart, CurfewEnd = airports[a].CurfewEnd,
                MaintenanceHubFor = mask, MaintenanceCost = cost,
            };
        }

        // ---- flights (one row per leg)
        var fH = Header(flightRows);
        foreach (var col in new[] { "FlightCode", "Kind", "From", "To", "Dep", "Arr" })
            if (!fH.ContainsKey(col)) Err("Flights", 1, $"Missing column '{col}'");
        if (msgs.Any(m => m.Severity == "error")) return new ReadResult(null, msgs);

        double Dist(int a, int b) => HaversineKm(airports[a].Lat, airports[a].Lon,
            airports[b].Lat, airports[b].Lon);

        var legs = new List<Leg>();
        var flights = new List<Flight>();
        var flightOrder = new List<string>();
        var byFlight = new Dictionary<string, List<(int Row, List<string> Cells)>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (row, cells) in flightRows.Skip(1))
        {
            string code = Cell(cells, fH, "FlightCode");
            if (code.Length == 0) continue;
            if (!byFlight.TryGetValue(code, out var list))
            { byFlight[code] = list = []; flightOrder.Add(code); }
            list.Add((row, cells));
        }
        foreach (var code in flightOrder)
        {
            var rows = byFlight[code];
            var first = rows[0];
            string kind = Cell(first.Cells, fH, "Kind").ToLowerInvariant();
            if (kind is not ("mandatory" or "optional" or "external"))
            {
                Err("Flights", first.Row, $"{code}: Kind must be mandatory, optional or external " +
                    $"(got '{kind}')");
                continue;
            }
            bool external = kind == "external";
            var legIds = new List<int>();
            bool bad = false;
            int flightId = flights.Count;
            foreach (var (row, cells) in rows)
            {
                string fromC = Cell(cells, fH, "From").ToUpperInvariant();
                string toC = Cell(cells, fH, "To").ToUpperInvariant();
                if (!apIdx.TryGetValue(fromC, out int from))
                { Err("Flights", row, $"{code}: unknown airport '{fromC}'"); bad = true; continue; }
                if (!apIdx.TryGetValue(toC, out int to))
                { Err("Flights", row, $"{code}: unknown airport '{toC}'"); bad = true; continue; }
                if (from == to) { Err("Flights", row, $"{code}: From equals To"); bad = true; continue; }
                int? dep = ParseTime(Cell(cells, fH, "Dep")), arr = ParseTime(Cell(cells, fH, "Arr"));
                if (dep is null || arr is null)
                {
                    Err("Flights", row, $"{code}: Dep/Arr must look like 'Mon 22:40' " +
                        $"(got '{Cell(cells, fH, "Dep")}' / '{Cell(cells, fH, "Arr")}')");
                    bad = true; continue;
                }
                double dist = Num(Cell(cells, fH, "DistanceKm")) ?? Math.Round(Dist(from, to), 1);
                if (legIds.Count > 0 && legs[legIds[^1]].Destination != from)
                {
                    Err("Flights", row, $"{code}: leg starts at {fromC} but the previous leg " +
                        $"arrives at {airports[legs[legIds[^1]].Destination].Code}");
                    bad = true; continue;
                }
                double varCost = Num(Cell(cells, fH, "VarCostPerT")) ?? Math.Round(dist * 0.035, 2);
                legs.Add(new Leg
                {
                    Id = legs.Count, FlightId = flightId, Origin = from, Destination = to,
                    Dep = dep.Value, Arr = arr.Value, DistanceKm = dist,
                    VariableCostPerTonne = varCost,
                    MaxWeight = external ? Num(Cell(cells, fH, "CapT")) ?? 0 : 0,
                    MaxVolume = external ? Num(Cell(cells, fH, "CapVolM3"))
                        ?? (Num(Cell(cells, fH, "CapT")) ?? 0) * 6 : 0,
                });
                legIds.Add(legs.Count - 1);
                if (external && legs[^1].MaxWeight <= 0)
                { Err("Flights", row, $"{code}: external legs need a positive CapT"); bad = true; }
            }
            if (bad || legIds.Count == 0)
            {
                // drop the flight and its legs to keep ids dense; report already emitted
                while (legIds.Count > 0) { legs.RemoveAt(legIds[^1]); legIds.RemoveAt(legIds.Count - 1); }
                continue;
            }
            var fixedCosts = new double[fleets.Count];
            if (!external)
            {
                double routeKm = legIds.Sum(l => legs[l].DistanceKm);
                for (int k = 0; k < fleets.Count; k++)
                {
                    double? v = Num(Cell(first.Cells, fH, $"Fixed:{fleets[k].Code}"));
                    fixedCosts[k] = v ?? Math.Round(routeKm * 4.0 + legIds.Count * 2000, 2);
                    if (v is null)
                        Warn("Flights", first.Row,
                            $"{code}: Fixed:{fleets[k].Code} blank, estimated {fixedCosts[k]:F0} $");
                }
            }
            flights.Add(new Flight
            {
                Id = flightId, Code = code, LegIds = [.. legIds], IsExternal = external,
                IsMandatory = kind == "mandatory",
                FixedCostByFleet = fixedCosts,
                ExternalFixedCost = external
                    ? Num(Cell(first.Cells, fH, "ExtFixedCost")) ?? 0 : 0,
            });
        }
        if (flights.Count == 0) Err("Flights", 0, "No valid flights");

        // ---- ODs
        var oH = Header(odRows);
        foreach (var col in new[] { "From", "To", "WeightT", "RatePerT" })
            if (!oH.ContainsKey(col)) Err("ODs", 1, $"Missing column '{col}'");
        var ods = new List<Od>();
        if (!msgs.Any(m => m.Severity == "error"))
            foreach (var (row, cells) in odRows.Skip(1))
            {
                string fromC = Cell(cells, oH, "From").ToUpperInvariant();
                string toC = Cell(cells, oH, "To").ToUpperInvariant();
                if (fromC.Length == 0 && toC.Length == 0) continue;
                if (!apIdx.TryGetValue(fromC, out int from))
                { Err("ODs", row, $"Unknown airport '{fromC}'"); continue; }
                if (!apIdx.TryGetValue(toC, out int to))
                { Err("ODs", row, $"Unknown airport '{toC}'"); continue; }
                if (from == to) { Err("ODs", row, "From equals To"); continue; }
                double? w = Num(Cell(cells, oH, "WeightT")), rate = Num(Cell(cells, oH, "RatePerT"));
                if (w is null or <= 0 || rate is null or < 0)
                { Err("ODs", row, $"{fromC}->{toC}: WeightT must be > 0 and RatePerT >= 0"); continue; }
                int avail = ParseTime(Cell(cells, oH, "Avail")) ?? 0;
                double delivH = Num(Cell(cells, oH, "MaxDeliveryH")) ?? 48;
                double vol = Num(Cell(cells, oH, "VolumeM3")) ?? w.Value * 6;
                ods.Add(new Od
                {
                    Id = ods.Count, Origin = from, Destination = to, Avail = avail,
                    MaxDeliveryTime = (int)(delivH * 60), Weight = w.Value, Volume = vol,
                    Rate = rate.Value,
                });
            }
        if (ods.Count == 0) Err("ODs", 0, "No valid O&D demand rows");

        if (msgs.Any(m => m.Severity == "error")) return new ReadResult(null, msgs);

        // optional Settings sheet (Key/Value): instance-level flags survive the round trip
        bool deliverAll = false;
        int handling = 0;
        if (sheets.TryGetValue("Settings", out var settingRows))
            foreach (var (_, cells) in settingRows.Skip(1))
            {
                if (cells.Count < 2) continue;
                var (key, val) = (cells[0].Trim(), cells[1].Trim());
                if (key.Equals("DeliverAll", StringComparison.OrdinalIgnoreCase))
                    deliverAll = IsYes(val);
                else if (key.Equals("CargoHandlingMinutes", StringComparison.OrdinalIgnoreCase))
                    handling = (int)(Num(val) ?? 0);
            }

        var inst = new Instance
        {
            Name = name, Period = Period.Weekly,
            DeliverAll = deliverAll, CargoHandlingMinutes = handling,
            Airports = [.. airports], Fleets = [.. fleets],
            Legs = [.. legs], Flights = [.. flights], Ods = [.. ods],
        };
        try { inst.Validate(); }
        catch (Exception ex)
        {
            Err("-", 0, $"Consistency check failed: {ex.Message}");
            return new ReadResult(null, msgs);
        }
        return new ReadResult(inst, msgs);
    }

    private static bool IsYes(string s) =>
        s.Equals("yes", StringComparison.OrdinalIgnoreCase) || s == "1" ||
        s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("y", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("si", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("sí", StringComparison.OrdinalIgnoreCase);

    /// <summary>Accepts 'Mon 22:40' (days Mon..Sun, case-insensitive) or a raw minute number.</summary>
    public static int? ParseTime(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return null;
        if (int.TryParse(s, out int min)) return min >= 0 && min < 10080 ? min : null;
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        int day = Array.FindIndex(Days, d => d.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (day < 0) return null;
        var hm = parts[1].Split(':');
        if (hm.Length != 2 || !int.TryParse(hm[0], out int h) || !int.TryParse(hm[1], out int m))
            return null;
        if (h is < 0 or > 23 || m is < 0 or > 59) return null;
        return day * 1440 + h * 60 + m;
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

    // ------------------------------------------------------- xlsx sheet parsing

    /// <summary>Parses sheet name -> rows (1-based row number, cells by column index).</summary>
    private static Dictionary<string, List<(int Row, List<string> Cells)>> ParseWorkbook(byte[] xlsx)
    {
        using var ms = new MemoryStream(xlsx);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        XDocument Doc(string path)
        {
            var e = zip.GetEntry(path) ?? throw new InvalidDataException($"missing {path}");
            using var s = e.Open();
            return XDocument.Load(s);
        }
        XNamespace m = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";

        // shared strings (Excel stores most text there; our own writer uses inline strings)
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is not null)
            foreach (var si in Doc("xl/sharedStrings.xml").Root!.Elements(m + "si"))
                shared.Add(string.Concat(si.Descendants(m + "t").Select(t => (string)t)));

        var relTargets = Doc("xl/_rels/workbook.xml.rels").Root!
            .Elements(rel + "Relationship")
            .ToDictionary(x => (string)x.Attribute("Id")!, x => (string)x.Attribute("Target")!);

        var result = new Dictionary<string, List<(int, List<string>)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sh in Doc("xl/workbook.xml").Root!.Element(m + "sheets")!.Elements(m + "sheet"))
        {
            string sheetName = (string)sh.Attribute("name")!;
            string target = relTargets[(string)sh.Attribute(r + "id")!].TrimStart('/');
            if (!target.StartsWith("xl/")) target = "xl/" + target;
            var doc = Doc(target);
            var rows = new List<(int, List<string>)>();
            foreach (var rowEl in doc.Root!.Element(m + "sheetData")!.Elements(m + "row"))
            {
                int rowNum = (int?)rowEl.Attribute("r") ?? rows.Count + 1;
                var cells = new List<string>();
                foreach (var c in rowEl.Elements(m + "c"))
                {
                    int col = ColIndex((string?)c.Attribute("r"), cells.Count);
                    while (cells.Count <= col) cells.Add("");
                    string t = (string?)c.Attribute("t") ?? "";
                    string v = t switch
                    {
                        "s" => int.TryParse((string?)c.Element(m + "v"), out int si)
                            && si < shared.Count ? shared[si] : "",
                        "inlineStr" => string.Concat(
                            (c.Element(m + "is")?.Descendants(m + "t") ?? [])
                            .Select(x => (string)x)),
                        _ => (string?)c.Element(m + "v") ?? "",
                    };
                    cells[col] = v;
                }
                rows.Add((rowNum, cells));
            }
            result[sheetName] = rows;
        }
        return result.ToDictionary(kv => kv.Key,
            kv => kv.Value.Select(x => (x.Item1, x.Item2)).ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static int ColIndex(string? cellRef, int fallback)
    {
        if (string.IsNullOrEmpty(cellRef)) return fallback;
        int col = 0;
        foreach (char ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') col = col * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') col = col * 26 + (ch - 'a' + 1);
            else break;
        }
        return col > 0 ? col - 1 : fallback;
    }
}
