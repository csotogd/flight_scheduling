using System.Collections.Concurrent;
using System.Text.Json;
using Acsp.Core;
using Acsp.Data;
using Acsp.Solver;

namespace Acsp.Web;

public sealed record SolveRequest(string Airline, int Set, int Seed, bool Maintenance,
    double TimeLimitSeconds, double GapTarget);

public sealed class SolveJob
{
    public required string Id { get; init; }
    public required SolveRequest Request { get; init; }
    public required string InstanceName { get; init; }
    public string Status { get; set; } = "running"; // running | done | failed
    public string? Error { get; set; }
    public ConcurrentQueue<object> Events { get; } = [];
    public List<object> EventLog { get; } = [];
    public object? Result { get; set; }
    public CancellationTokenSource Cancel { get; } = new();
}

public sealed class SolveJobManager
{
    private readonly ConcurrentDictionary<string, SolveJob> _jobs = new();

    public IEnumerable<object> List() => _jobs.Values
        .OrderByDescending(j => j.Id)
        .Select(j => new { id = j.Id, instance = j.InstanceName, status = j.Status,
            maintenance = j.Request.Maintenance, error = j.Error });

    public SolveJob? Get(string id) => _jobs.GetValueOrDefault(id);

    public SolveJob Start(SolveRequest req, Instance? prebuilt = null)
    {
        var inst = prebuilt ?? InstanceGenerator.Generate(req.Airline, req.Set, req.Seed);
        var job = new SolveJob
        {
            Id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            Request = req,
            InstanceName = inst.Name + (req.Maintenance ? "+MC" : ""),
        };
        _jobs[job.Id] = job;
        _ = Task.Run(() => Run(job, inst));
        return job;
    }

    private static void Run(SolveJob job, Instance inst)
    {
        try
        {
            var bpc = new BranchAndPrice(inst, new BpcOptions
            {
                WithMaintenance = job.Request.Maintenance,
                GapTarget = job.Request.GapTarget,
                TimeLimitSeconds = job.Request.TimeLimitSeconds,
                MipHeuristicTimeLimit = Math.Max(20, inst.Flights.Length / 12),
            });
            var lastEvent = DateTime.MinValue;
            bpc.Progress += p =>
            {
                bool important = p.Phase.StartsWith("incumbent");
                if (!important && (DateTime.Now - lastEvent).TotalMilliseconds < 400) return;
                lastEvent = DateTime.Now;
                var e = new
                {
                    type = "progress",
                    t = Math.Round(p.ElapsedSeconds, 1),
                    nodes = p.NodesExplored,
                    incumbent = Finite(p.Incumbent),
                    bound = Finite(p.Bound),
                    gap = Finite(p.Gap),
                    cols = p.Paths + p.Strings,
                    cuts = p.Cuts,
                    phase = p.Phase,
                };
                job.Events.Enqueue(e);
                lock (job.EventLog) job.EventLog.Add(e);
            };
            var res = bpc.Solve(job.Cancel.Token);
            if (res.Best is not null)
            {
                job.Result = SolutionJson.Build(inst, res);
                SolutionJson.Save(inst, res, Path.Combine(ResultsDir(),
                    inst.Name + (job.Request.Maintenance ? "-mnt" : "") + ".solution.json"));
            }
            job.Status = res.Best is null ? "failed" : "done";
            job.Error = res.Best is null ? $"no solution found ({res.StopReason})" : null;
        }
        catch (Exception ex)
        {
            job.Status = "failed";
            job.Error = ex.Message;
        }
        var final = new { type = "status", status = job.Status, error = job.Error };
        job.Events.Enqueue(final);
        lock (job.EventLog) job.EventLog.Add(final);
    }

    private static double? Finite(double v) => double.IsFinite(v) ? Math.Round(v, 4) : null;

    /// <summary>Results directory at the repo root (cwd of dotnet run varies).</summary>
    public static string ResultsDir()
    {
        var dir = Directory.GetCurrentDirectory();
        for (var d = dir; d is not null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "Acsp.sln")))
                return Path.Combine(d, "results");
        return Path.Combine(dir, "results");
    }
}
