using Acsp.Core;
using Acsp.Solver.Lp;

namespace Acsp.Solver;

/// <summary>
/// The restricted master problem of model ACSP-T (§3.4): a single persistent LP kept in memory
/// throughout branch and price and cut (§5). Columns (cargo flow paths, flight strings, ground
/// arcs, external flight selectors) and rows (demand, capacities, cover, flow balance, fleet
/// size, implied bound cuts) are indexed here; branching disables columns via bounds instead of
/// removing them.
/// </summary>
public sealed class Rmp : IDisposable
{
    private const double Inf = double.PositiveInfinity;
    private readonly Instance _inst;
    private readonly ILpSolver _lp;
    public TimelineNetwork Network { get; }
    public bool WithMaintenance { get; }

    // row indices
    private readonly int[] _odRow;
    private readonly int[] _legWeightRow;
    private readonly int[] _legVolumeRow;
    private readonly int[] _coverRow;      // per cargo flight id, -1 otherwise
    private readonly int[] _eventRow;      // per timeline event
    private readonly int[] _fleetRow;
    private readonly Dictionary<(int Od, int Flight), int> _cutRow = [];

    // column bookkeeping
    public sealed record PathCol(CargoPath Path, int Col);
    public sealed record StringCol(FlightString Str, int Col, int Chi);
    private readonly List<PathCol> _paths = [];
    private readonly List<StringCol> _strings = [];
    private readonly Dictionary<string, int> _pathIndex = [];
    private readonly Dictionary<string, int> _stringIndex = [];
    private readonly Dictionary<int, int> _extCol = [];     // external flight id -> column
    private readonly List<(int Od, int Flight)> _cuts = [];
    private readonly List<int> _artificialCols = [];
    private int[]? _recourseCol; // deliver-all: contracted delivery column per od
    private const double BigM = 1e7;

    public IReadOnlyList<PathCol> Paths => _paths;
    public IReadOnlyList<StringCol> Strings => _strings;
    /// <summary>Booking columns of external flights with fixed costs (flight id -> column).</summary>
    public IReadOnlyDictionary<int, int> ExternalColumns => _extCol;
    public IReadOnlyList<(int Od, int Flight)> Cuts => _cuts;
    public int CutCount => _cuts.Count;

    public Rmp(Instance inst, bool withMaintenance, ILpSolver lp)
    {
        _inst = inst;
        _lp = lp;
        WithMaintenance = withMaintenance;
        Network = new TimelineNetwork(inst, withMaintenance);

        // deliver-all: demand rows are equalities and every od carries an always-available
        // contracted-delivery column (ExternalRecourse pricing), so serving everything is
        // feasible by construction and the model trades own flying against the contract bill
        _odRow = new int[inst.Ods.Length];
        foreach (var od in inst.Ods)
            _odRow[od.Id] = inst.DeliverAll
                ? _lp.AddRow(od.Weight, od.Weight, [], [])
                : _lp.AddRow(-Inf, od.Weight, [], []);
        if (inst.DeliverAll)
        {
            var recourse = ExternalRecourse.CostPerTonne(inst);
            _recourseCol = new int[inst.Ods.Length];
            foreach (var od in inst.Ods)
            {
                Span<int> r = [_odRow[od.Id]];
                Span<double> c = [1.0];
                _recourseCol[od.Id] = _lp.AddColumn(od.Rate - recourse[od.Id], 0, od.Weight, r, c);
            }
        }

        _legWeightRow = new int[inst.Legs.Length];
        _legVolumeRow = new int[inst.Legs.Length];
        foreach (var leg in inst.Legs)
        {
            bool ext = inst.Flights[leg.FlightId].IsExternal;
            bool bookable = ext && inst.Flights[leg.FlightId].ExternalFixedCost > 0;
            // (15)/(16) for external legs (right-hand side 0 when a booking variable exists, §4.1),
            // (17)/(18) for cargo legs (string columns add -wmax/-vmax coefficients later)
            double wCap = ext && !bookable ? leg.MaxWeight : 0;
            double vCap = ext && !bookable ? leg.MaxVolume : 0;
            _legWeightRow[leg.Id] = _lp.AddRow(-Inf, wCap, [], []);
            _legVolumeRow[leg.Id] = _lp.AddRow(-Inf, vCap, [], []);
        }

        _coverRow = new int[inst.Flights.Length];
        Array.Fill(_coverRow, -1);
        foreach (var f in inst.CargoFlights)
            _coverRow[f.Id] = _lp.AddRow(f.IsMandatory ? 1 : 0, 1, [], []);

        _eventRow = new int[Network.NumEvents];
        for (int e = 0; e < Network.NumEvents; e++)
            _eventRow[e] = _lp.AddRow(0, 0, [], []);

        _fleetRow = new int[inst.Fleets.Length];
        foreach (var k in inst.Fleets)
            _fleetRow[k.Id] = _lp.AddRow(-Inf, k.Count, [], []);

        // ground arc columns
        foreach (var g in Network.GroundArcs)
        {
            // self-loop (single event at the airport): balance coefficients cancel, keep fleet row
            Span<int> rows = g.FromEvent == g.ToEvent
                ? [_fleetRow[g.Fleet]]
                : [_eventRow[g.FromEvent], _eventRow[g.ToEvent], _fleetRow[g.Fleet]];
            Span<double> coefs = g.FromEvent == g.ToEvent ? [g.Chi] : [1.0, -1.0, g.Chi];
            _lp.AddColumn(-_inst.Fleets[g.Fleet].FixedCostPerAircraft * g.Chi, 0, Inf, rows, coefs);
        }

        // artificial columns keep the RMP feasible while columns are still missing (colgen
        // phase-1): cover rows may be satisfied and fleet rows relaxed at a large penalty.
        foreach (var f in inst.CargoFlights)
        {
            Span<int> r = [_coverRow[f.Id]];
            Span<double> c = [1.0];
            _artificialCols.Add(_lp.AddColumn(-BigM, 0, 1, r, c));
        }
        foreach (var k in inst.Fleets)
        {
            Span<int> r = [_fleetRow[k.Id]];
            Span<double> c = [-1.0];
            _artificialCols.Add(_lp.AddColumn(-BigM, 0, Inf, r, c));
        }

        // booking variables for external flights with fixed costs (§4.1)
        foreach (var f in inst.ExternalFlights.Where(f => f.ExternalFixedCost > 0))
        {
            var rows = new List<int>();
            var coefs = new List<double>();
            foreach (var lid in f.LegIds)
            {
                rows.Add(_legWeightRow[lid]); coefs.Add(-inst.Legs[lid].MaxWeight);
                rows.Add(_legVolumeRow[lid]); coefs.Add(-inst.Legs[lid].MaxVolume);
            }
            int col = _lp.AddColumn(-f.ExternalFixedCost, 0, 1,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows),
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(coefs));
            _lp.SetInteger(col, true);
            _extCol[f.Id] = col;
        }
    }

    // ---------------------------------------------------------------- columns

    /// <summary>Adds a cargo flow path column; returns false if it already exists.</summary>
    /// <summary>Whether this exact column is already in the master (then its true reduced
    /// cost is &lt;= 0 at LP optimality, whatever a pricer claims).</summary>
    public bool ContainsPath(CargoPath p) => _pathIndex.ContainsKey(p.Key());
    public bool ContainsString(FlightString s) => _stringIndex.ContainsKey(s.Key());

    public bool AddPath(CargoPath p)
    {
        var key = p.Key();
        if (_pathIndex.ContainsKey(key)) return false;
        var od = _inst.Ods[p.OdId];
        var rows = new List<int> { _odRow[p.OdId] };
        var coefs = new List<double> { 1.0 };
        foreach (var lid in p.LegIds)
        {
            rows.Add(_legWeightRow[lid]); coefs.Add(1.0);
            rows.Add(_legVolumeRow[lid]); coefs.Add(od.VolumePerTonne);
        }
        foreach (var fid in p.LegIds.Select(l => _inst.Legs[l].FlightId).Distinct())
            if (_cutRow.TryGetValue((p.OdId, fid), out int cr))
            { rows.Add(cr); coefs.Add(1.0); }
        int col = _lp.AddColumn(p.Margin(_inst), 0, Inf,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows),
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(coefs));
        _paths.Add(new PathCol(p, col));
        _pathIndex[key] = _paths.Count - 1;
        return true;
    }

    /// <summary>Adds a flight string column; returns false if it already exists.</summary>
    public bool AddString(FlightString s)
    {
        var key = s.Key();
        if (_stringIndex.ContainsKey(key)) return false;
        int chi = Network.ChiOfString(s);
        int k = s.FleetId;
        var rows = new List<int>();
        var coefs = new List<double>();
        foreach (var fid in s.FlightIds)
        {
            rows.Add(_coverRow[fid]); coefs.Add(1.0);
            foreach (var lid in _inst.Flights[fid].LegIds)
            {
                rows.Add(_legWeightRow[lid]); coefs.Add(-_inst.Fleets[k].MaxWeight);
                rows.Add(_legVolumeRow[lid]); coefs.Add(-_inst.Fleets[k].MaxVolume);
            }
            foreach (var (od, fl) in _cuts)
                if (fl == fid)
                { rows.Add(_cutRow[(od, fl)]); coefs.Add(-_inst.Ods[od].Weight); }
        }
        rows.Add(_eventRow[Network.DepEvent[k, s.FlightIds[0]]]); coefs.Add(1.0);
        rows.Add(_eventRow[Network.ArrEvent[k, s.FlightIds[^1]]]); coefs.Add(-1.0);
        if (chi != 0) { rows.Add(_fleetRow[k]); coefs.Add(chi); }
        double obj = -s.Cost(_inst, WithMaintenance) - chi * _inst.Fleets[k].FixedCostPerAircraft;
        int col = _lp.AddColumn(obj, 0, 1,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows),
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(coefs));
        _lp.SetInteger(col, true);
        _strings.Add(new StringCol(s, col, chi));
        _stringIndex[key] = _strings.Count - 1;
        return true;
    }

    /// <summary>Adds the implied bound cut (35) for (od, optional flight) over existing columns.</summary>
    public bool AddImpliedBoundCut(int od, int flight)
    {
        if (_cutRow.ContainsKey((od, flight))) return false;
        double dod = _inst.Ods[od].Weight;
        var cols = new List<int>();
        var coefs = new List<double>();
        foreach (var pc in _paths)
            if (pc.Path.OdId == od &&
                pc.Path.LegIds.Any(l => _inst.Legs[l].FlightId == flight))
            { cols.Add(pc.Col); coefs.Add(1.0); }
        foreach (var sc in _strings)
            if (sc.Str.FlightIds.Contains(flight))
            { cols.Add(sc.Col); coefs.Add(-dod); }
        int row = _lp.AddRow(-Inf, 0,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cols),
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(coefs));
        _cutRow[(od, flight)] = row;
        _cuts.Add((od, flight));
        return true;
    }

    /// <summary>
    /// Seeds the RMP with trivial (single-flight) strings so that an initial feasible solution
    /// exists (§5). With maintenance, only maintenance-feasible trivial strings are added.
    /// </summary>
    public int SeedTrivialStrings()
    {
        int added = 0;
        foreach (var f in _inst.CargoFlights)
        {
            bool any = false;
            for (int k = 0; k < _inst.Fleets.Length; k++)
            {
                if (!_inst.Compatible(k, f.Id)) continue;
                var s = new FlightString { FleetId = k, FlightIds = [f.Id] };
                if (!s.IsFeasible(_inst, WithMaintenance, out _)) continue;
                if (AddString(s)) added++;
                any = true;
            }
            if (!any && f.IsMandatory)
                throw new InvalidOperationException(
                    $"mandatory flight {f.Code} has no feasible trivial string; instance is infeasible");
        }
        return added;
    }

    // ---------------------------------------------------------------- branching state

    /// <summary>
    /// Applies the branching state of a node: disables incompatible columns via bounds and
    /// adjusts cover rows / external booking bounds. Everything is restorable by re-applying
    /// with a different state.
    /// </summary>
    public void ApplyBranchingState(PricingRestrictions rest,
        IReadOnlyDictionary<int, bool>? forcedFlights = null,
        IReadOnlyDictionary<int, bool>? forcedExternals = null,
        IReadOnlyDictionary<string, bool>? fixedStrings = null)
    {
        foreach (var pc in _paths)
            _lp.SetColumnBounds(pc.Col, 0, rest.Allows(pc.Path) ? Inf : 0);
        foreach (var sc in _strings)
        {
            double lo = 0, hi = rest.Allows(sc.Str) ? 1 : 0;
            if (fixedStrings is not null && fixedStrings.TryGetValue(sc.Str.Key(), out bool fixVal))
            { lo = fixVal ? 1 : 0; hi = fixVal ? 1 : 0; }
            _lp.SetColumnBounds(sc.Col, lo, hi);
        }
        foreach (var f in _inst.CargoFlights)
        {
            double lo = f.IsMandatory ? 1 : 0, hi = 1;
            if (forcedFlights is not null && forcedFlights.TryGetValue(f.Id, out bool sel))
            { lo = sel ? 1 : 0; hi = sel ? 1 : 0; }
            _lp.SetRowBounds(_coverRow[f.Id], lo, hi);
        }
        foreach (var (fid, col) in _extCol)
        {
            double lo = 0, hi = 1;
            if (forcedExternals is not null && forcedExternals.TryGetValue(fid, out bool sel))
            { lo = sel ? 1 : 0; hi = sel ? 1 : 0; }
            _lp.SetColumnBounds(col, lo, hi);
        }
    }

    // ---------------------------------------------------------------- solving

    public LpResult SolveLp() => _lp.SolveLp();

    /// <summary>
    /// Monetizes a fixed schedule: pins every string (and external booking) to the given
    /// solution's selection and solves the LP, so the cargo flows are optimized over the
    /// column pool generated so far. One warm LP solve, no pricing. Bounds are restored to
    /// [0,1] afterwards (root state; callers under branching must reapply their state).
    /// </summary>
    public LpResult SolveLpWithSelectionFixed(Solution sol)
    {
        var want = sol.SelectedStrings.Select(s => s.Key()).ToHashSet();
        foreach (var sc in _strings)
        {
            double v = want.Contains(sc.Str.Key()) ? 1 : 0;
            _lp.SetColumnBounds(sc.Col, v, v);
        }
        foreach (var (fid, col) in _extCol)
        {
            double v = sol.SelectedExternalFlights.Contains(fid) ? 1 : 0;
            _lp.SetColumnBounds(col, v, v);
        }
        var lp = SolveLp();
        foreach (var sc in _strings) _lp.SetColumnBounds(sc.Col, 0, 1);
        foreach (var (_, col) in _extCol) _lp.SetColumnBounds(col, 0, 1);
        return lp;
    }

    public LpResult SolveMipOnCurrentColumns(double timeLimitSeconds, double gap = 1e-4,
        Solution? mipStart = null) =>
        _lp.SolveMip(timeLimitSeconds, gap, mipStart is null ? null : BuildMipStart(mipStart));

    /// <summary>Sparse (column, value) warm start from a known feasible solution: selected
    /// strings and external bookings at 1, path flows at their tonnes. Columns of the solution
    /// that are not in the RMP are skipped (the start is partial anyway).</summary>
    private List<(int Col, double Value)> BuildMipStart(Solution sol)
    {
        var start = new List<(int, double)>();
        foreach (var s in sol.SelectedStrings)
            if (_stringIndex.TryGetValue(s.Key(), out int i))
                start.Add((_strings[i].Col, 1.0));
        foreach (var (path, tonnes) in sol.Flows)
            if (_pathIndex.TryGetValue(path.Key(), out int i))
                start.Add((_paths[i].Col, tonnes));
        foreach (var f in sol.SelectedExternalFlights)
            if (_extCol.TryGetValue(f, out int col))
                start.Add((col, 1.0));
        return start;
    }

    public MasterDuals GetDuals(LpResult res)
    {
        var d = MasterDuals.Zero(_inst);
        foreach (var od in _inst.Ods) d.OdDemand[od.Id] = res.RowDuals[_odRow[od.Id]];
        foreach (var leg in _inst.Legs)
        {
            d.LegWeight[leg.Id] = res.RowDuals[_legWeightRow[leg.Id]];
            d.LegVolume[leg.Id] = res.RowDuals[_legVolumeRow[leg.Id]];
        }
        foreach (var f in _inst.CargoFlights)
            d.FlightCover[f.Id] = res.RowDuals[_coverRow[f.Id]];
        for (int k = 0; k < _inst.Fleets.Length; k++)
        {
            d.FleetSize[k] = res.RowDuals[_fleetRow[k]];
            foreach (var f in _inst.CargoFlights)
            {
                if (Network.DepEvent[k, f.Id] >= 0)
                    d.DepBalance[k, f.Id] = res.RowDuals[_eventRow[Network.DepEvent[k, f.Id]]];
                if (Network.ArrEvent[k, f.Id] >= 0)
                    d.ArrBalance[k, f.Id] = res.RowDuals[_eventRow[Network.ArrEvent[k, f.Id]]];
            }
        }
        foreach (var (key, row) in _cutRow)
            d.ImpliedBoundCuts[key] = res.RowDuals[row];
        return d;
    }

    // ---------------------------------------------------------------- solution extraction

    /// <summary>Total artificial-column usage; above tolerance the node is truly infeasible.</summary>
    public double ArtificialUsage(LpResult res) =>
        _artificialCols.Sum(c => res.ColumnValues[c]);

    public bool IsIntegral(LpResult res, double tol = 1e-6)
    {
        if (ArtificialUsage(res) > tol) return false;
        foreach (var sc in _strings)
        {
            double v = res.ColumnValues[sc.Col];
            if (v > tol && v < 1 - tol) return false;
        }
        foreach (var col in _extCol.Values)
        {
            double v = res.ColumnValues[col];
            if (v > tol && v < 1 - tol) return false;
        }
        return true;
    }

    public Solution ExtractSolution(LpResult res, double tol = 1e-6)
    {
        var strings = _strings.Where(sc => res.ColumnValues[sc.Col] > 1 - tol)
            .Select(sc => sc.Str).ToList();
        var flows = _paths.Where(pc => res.ColumnValues[pc.Col] > tol)
            .Select(pc => (pc.Path, res.ColumnValues[pc.Col])).ToList();
        var ext = _extCol.Where(kv => res.ColumnValues[kv.Value] > 1 - tol)
            .Select(kv => kv.Key).ToHashSet();
        var contracted = new List<(int, double)>();
        if (_recourseCol is not null)
            foreach (var od in _inst.Ods)
                if (res.ColumnValues[_recourseCol[od.Id]] > tol)
                    contracted.Add((od.Id, res.ColumnValues[_recourseCol[od.Id]]));
        return new Solution
        {
            Contracted = contracted,
            SelectedStrings = strings,
            Flows = flows,
            SelectedExternalFlights = ext,
            WithMaintenance = WithMaintenance,
        };
    }

    /// <summary>Violated implied bound cuts in the current solution, most violated first (§8).</summary>
    public List<(int Od, int Flight, double Violation)> SeparateImpliedBoundCuts(LpResult res)
    {
        // flow per (od, optional flight) and string cover per optional flight
        var flowByOdFlight = new Dictionary<(int, int), double>();
        foreach (var pc in _paths)
        {
            double x = res.ColumnValues[pc.Col];
            if (x <= 1e-9) continue;
            foreach (var fid in pc.Path.LegIds.Select(l => _inst.Legs[l].FlightId).Distinct())
            {
                if (!_inst.Flights[fid].IsOptionalCargo) continue;
                var key = (pc.Path.OdId, fid);
                flowByOdFlight[key] = flowByOdFlight.GetValueOrDefault(key) + x;
            }
        }
        var coverByFlight = new double[_inst.Flights.Length];
        foreach (var sc in _strings)
        {
            double y = res.ColumnValues[sc.Col];
            if (y <= 1e-9) continue;
            foreach (var fid in sc.Str.FlightIds) coverByFlight[fid] += y;
        }
        var violated = new List<(int, int, double)>();
        foreach (var ((od, fid), flow) in flowByOdFlight)
        {
            if (_cutRow.ContainsKey((od, fid))) continue;
            double rhs = coverByFlight[fid] * _inst.Ods[od].Weight;
            double violation = flow - rhs;
            if (violation > 1e-6) violated.Add((od, fid, violation));
        }
        return violated.OrderByDescending(v => v.Item3).ToList();
    }

    public void Dispose() => _lp.Dispose();
}
