using System.Text;
using System.Text.Json;
using Acsp.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SolveJobManager>();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

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

// previously saved solutions on disk
app.MapGet("/api/solutions", () =>
{
    var dir = "results";
    if (!Directory.Exists(dir)) return Results.Json(Array.Empty<object>());
    return Results.Json(Directory.GetFiles(dir, "*.solution.json")
        .OrderBy(f => f)
        .Select(f => new { name = Path.GetFileName(f).Replace(".solution.json", "") }));
});

app.MapGet("/api/solutions/{name}", (string name) =>
{
    var path = Path.Combine("results", name + ".solution.json");
    if (!File.Exists(path) || name.Contains("..")) return Results.NotFound();
    return Results.Text(File.ReadAllText(path), "application/json", Encoding.UTF8);
});

app.Run("http://localhost:5170");
