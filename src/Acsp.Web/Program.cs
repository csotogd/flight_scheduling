using System.Text;
using System.Text.Json;
using Acsp.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SolveJobManager>();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

// resolve the results directory at the repo root (dotnet run may set cwd to the project dir)
static string ResultsDir()
{
    var dir = Directory.GetCurrentDirectory();
    for (var d = dir; d is not null; d = Path.GetDirectoryName(d))
        if (File.Exists(Path.Combine(d, "Acsp.sln")))
            return Path.Combine(d, "results");
    return Path.Combine(dir, "results");
}

app.UseDefaultFiles();
app.UseStaticFiles();

var json = new JsonSerializerOptions { PropertyNamingPolicy = null };

app.MapGet("/api/profiles", () => Results.Json(new
{
    airlines = new[]
    {
        new { code = "RC", name = "Regional cargo (HKG, 9xA300F)" },
        new { code = "IC", name = "International cargo (LUX, 16xB747F + RFS)" },
        new { code = "MI", name = "Mixed carrier (SIN/BRU, 23 a/c + PAX bellies)" },
        new { code = "EX", name = "Express (BRU/BAH/PHL/PTY, 84 a/c)" },
    },
    sets = new[] { 1, 2, 3 },
}));

app.MapGet("/api/jobs", (SolveJobManager jobs) => Results.Json(jobs.List()));

app.MapPost("/api/solve", (SolveRequest req, SolveJobManager jobs) =>
{
    var job = jobs.Start(req);
    return Results.Json(new { id = job.Id, instance = job.InstanceName });
});

app.MapPost("/api/jobs/{id}/cancel", (string id, SolveJobManager jobs) =>
{
    var job = jobs.Get(id);
    if (job is null) return Results.NotFound();
    job.Cancel.Cancel();
    return Results.Ok();
});

app.MapGet("/api/jobs/{id}/events", async (string id, SolveJobManager jobs, HttpContext ctx) =>
{
    var job = jobs.Get(id);
    if (job is null) { ctx.Response.StatusCode = 404; return; }
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    int sent = 0;
    while (!ctx.RequestAborted.IsCancellationRequested)
    {
        object[] pending;
        lock (job.EventLog) pending = job.EventLog.Skip(sent).ToArray();
        foreach (var e in pending)
        {
            await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(e)}\n\n", ctx.RequestAborted);
            sent++;
        }
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        if (job.Status != "running" && sent >= EventCount(job)) break;
        await Task.Delay(300, ctx.RequestAborted);
    }

    static int EventCount(SolveJob job) { lock (job.EventLog) return job.EventLog.Count; }
});

app.MapGet("/api/jobs/{id}/result", (string id, SolveJobManager jobs) =>
{
    var job = jobs.Get(id);
    if (job is null) return Results.NotFound();
    if (job.Result is null) return Results.Json(new { status = job.Status, error = job.Error });
    return Results.Json(job.Result);
});

// planner assistant: propose optional flights for stranded demand and re-optimize (§2.1 paradigm)
app.MapPost("/api/propose", (SolveRequest req, SolveJobManager jobs) =>
{
    var inst = Acsp.Data.InstanceGenerator.Generate(req.Airline, req.Set, req.Seed);
    var solPath = Path.Combine(ResultsDir(),
        inst.Name + (req.Maintenance ? "-mnt" : "") + ".solution.json");
    var shipped = new double[inst.Ods.Length];
    if (File.Exists(solPath))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(solPath));
        foreach (var od in doc.RootElement.GetProperty("ods").EnumerateArray())
            shipped[od.GetProperty("id").GetInt32()] = od.GetProperty("shippedT").GetDouble();
    }
    var result = Acsp.Solver.FlightProposer.Propose(inst, shipped);
    var job = jobs.Start(req, result.Extended);
    return Results.Json(new
    {
        id = job.Id,
        instance = job.InstanceName,
        proposals = result.Proposals,
        unservableBefore = result.UnservableBefore,
        unservableAfter = result.UnservableAfter,
        tonnesBefore = result.TonnesBefore,
        tonnesAfter = result.TonnesAfter,
    });
});

// itinerary download as a real .xlsx (rotations + cargo flows + P&L)
app.MapGet("/api/solutions/{name}/itinerary.xlsx", (string name) =>
{
    var path = Path.Combine(ResultsDir(), name + ".solution.json");
    if (!File.Exists(path) || name.Contains("..")) return Results.NotFound();
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var root = doc.RootElement;
    int N = root.GetProperty("periodMinutes").GetInt32();
    var days = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
    string T(int t) => $"{days[t / 1440 % 7]} {t % 1440 / 60:D2}:{t % 60:D2}";
    var apCode = root.GetProperty("airports").EnumerateArray()
        .ToDictionary(a => a.GetProperty("id").GetInt32(), a => a.GetProperty("code").GetString()!);
    var flights = root.GetProperty("flights").EnumerateArray()
        .ToDictionary(f => f.GetProperty("id").GetInt32(), f => f);
    var flightByCode = root.GetProperty("flights").EnumerateArray()
        .ToDictionary(f => f.GetProperty("code").GetString()!, f => f);
    var legOwner = new Dictionary<int, (string Code, JsonElement Leg)>();
    foreach (var f in root.GetProperty("flights").EnumerateArray())
        foreach (var l in f.GetProperty("legs").EnumerateArray())
            legOwner[l.GetProperty("id").GetInt32()] = (f.GetProperty("code").GetString()!, l);

    var rotRows = new List<object?[]>();
    rotRows.Add(new object?[] { "Rotation", "Fleet", "Aircraft", "Flight", "From", "To",
        "Departure", "Arrival", "Load t", "Cap t" });
    foreach (var r in root.GetProperty("rotations").EnumerateArray())
    {
        var rot = $"R{r.GetProperty("id").GetInt32() + 1}";
        var fleet = r.GetProperty("fleet").GetString();
        var aircraft = r.GetProperty("aircraft").GetInt32();
        foreach (var s in r.GetProperty("strings").EnumerateArray())
            foreach (var fid in s.GetProperty("flightIds").EnumerateArray())
            {
                var f = flights[fid.GetInt32()];
                foreach (var l in f.GetProperty("legs").EnumerateArray())
                    rotRows.Add(new object?[] { rot, fleet, aircraft, f.GetProperty("code").GetString(),
                        apCode[l.GetProperty("from").GetInt32()], apCode[l.GetProperty("to").GetInt32()],
                        T(l.GetProperty("dep").GetInt32()), T(l.GetProperty("arr").GetInt32()),
                        l.GetProperty("loadT").GetDouble(), l.GetProperty("capT").GetDouble() });
            }
    }

    var flowRows = new List<object?[]>();
    flowRows.Add(new object?[] { "O&D", "From", "To", "Tonnes", "Route (flights)",
        "Route (airports)", "Departure", "Arrival" });
    var ods = root.GetProperty("ods").EnumerateArray()
        .ToDictionary(o => o.GetProperty("id").GetInt32(), o => o);
    foreach (var fl in root.GetProperty("flows").EnumerateArray())
    {
        var od = ods[fl.GetProperty("od").GetInt32()];
        var legIds = fl.GetProperty("legs").EnumerateArray().Select(x => x.GetInt32()).ToList();
        var legs = legIds.Select(id => legOwner[id]).ToList();
        var routeAirports = new List<string> { apCode[legs[0].Leg.GetProperty("from").GetInt32()] };
        routeAirports.AddRange(legs.Select(x => apCode[x.Leg.GetProperty("to").GetInt32()]));
        flowRows.Add(new object?[]
        {
            fl.GetProperty("od").GetInt32(),
            apCode[od.GetProperty("from").GetInt32()], apCode[od.GetProperty("to").GetInt32()],
            fl.GetProperty("tonnes").GetDouble(),
            string.Join(" / ", legs.Select(x => x.Code).Distinct()),
            string.Join(" → ", routeAirports),
            T(legs[0].Leg.GetProperty("dep").GetInt32()),
            T(legs[^1].Leg.GetProperty("arr").GetInt32()),
        });
    }

    var pnl = root.GetProperty("pnl");
    var stats = root.GetProperty("stats");
    var kpiRows = new List<object?[]>
    {
        new object?[] { "Instance", root.GetProperty("instance").GetString() },
        new object?[] { "Profit", pnl.GetProperty("profit").GetDouble() },
        new object?[] { "Revenue", pnl.GetProperty("revenue").GetDouble() },
        new object?[] { "Variable costs", pnl.GetProperty("variableCosts").GetDouble() },
        new object?[] { "Fixed flight costs", pnl.GetProperty("fixedFlightCosts").GetDouble() },
        new object?[] { "Aircraft costs", pnl.GetProperty("aircraftCosts").GetDouble() },
        new object?[] { "Gap", stats.GetProperty("gap").GetDouble() },
        new object?[] { "B&B nodes", stats.GetProperty("nodes").GetInt32() },
        new object?[] { "Seconds", stats.GetProperty("seconds").GetDouble() },
    };

    var bytes = XlsxWriter.Build(
        ("Rotations", rotRows), ("OD flows", flowRows), ("KPIs", kpiRows));
    return Results.File(bytes,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        name + "-itinerary.xlsx");
});

// previously saved solutions on disk
app.MapGet("/api/solutions", () =>
{
    var dir = ResultsDir();
    if (!Directory.Exists(dir)) return Results.Json(Array.Empty<object>());
    return Results.Json(Directory.GetFiles(dir, "*.solution.json")
        .OrderBy(f => f)
        .Select(f => new { name = Path.GetFileName(f).Replace(".solution.json", "") }));
});

app.MapGet("/api/solutions/{name}", (string name) =>
{
    var path = Path.Combine(ResultsDir(), name + ".solution.json");
    if (!File.Exists(path) || name.Contains("..")) return Results.NotFound();
    return Results.Text(File.ReadAllText(path), "application/json", Encoding.UTF8);
});

app.Run("http://localhost:5170");
