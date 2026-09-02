using System.Collections.Concurrent;
using System.Text.Json;
using Acsp.Core;
using Acsp.Data;
using Acsp.Solver;

namespace Acsp.Web;

public sealed record SolveRequest(string Airline, int Set, int Seed, bool Maintenance,
    double TimeLimitSeconds, double GapTarget, string? UploadId = null,
    bool Regional = false);

public sealed record DesignRequest(string Airline, int Set, int Seed, bool Maintenance,
    double RoundTimeLimitSeconds, double GapTarget, string? UploadId,
    int Batch, int MaxRounds, double StopThreshold, int EvictAfter,
    bool Regional = false);

/// <summary>Instances uploaded as Excel workbooks, kept in memory for this server session.</summary>
public sealed class UploadStore
{
    private readonly ConcurrentDictionary<string, Instance> _uploads = new();

    public string Add(Instance inst)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        _uploads[id] = inst;
        return id;
    }

    public Instance? Get(string? id) => id is null ? null : _uploads.GetValueOrDefault(id);
}

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

    public SolveJob StartDesign(DesignRequest req, Instance inst)
    {
        var job = new SolveJob
        {
            Id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            Request = new SolveRequest(req.Airline, req.Set, req.Seed, req.Maintenance,
                req.RoundTimeLimitSeconds, req.GapTarget, req.UploadId),
            InstanceName = inst.Name + "+design" + (req.Maintenance ? "+MC" : ""),
        };
        _jobs[job.Id] = job;
        _ = Task.Run(() => RunDesign(job, req, inst));
        return job;
    }

    private static void RunDesign(SolveJob job, DesignRequest req, Instance inst)
    {
        void Emit(object e)
        {
            job.Events.Enqueue(e);
            lock (job.EventLog) job.EventLog.Add(e);
        }
        try
        {
            var designer = new NetworkDesigner(inst, new DesignOptions
            {
                BatchSize = req.Batch,
                MaxRounds = req.MaxRounds,
                StopThreshold = req.StopThreshold,
                EvictAfterRounds = req.EvictAfter,
                RoundTimeLimitSeconds = req.RoundTimeLimitSeconds,
                GapTarget = req.GapTarget,
                WithMaintenance = req.Maintenance,
                RegionalPolish = req.Regional,
            });
            var lastEvent = DateTime.MinValue;
            designer.Progress += p =>
            {
                if (p.Solver is { } s)
                {
                    bool important = s.Phase.StartsWith("incumbent");
                    if (!important && (DateTime.Now - lastEvent).TotalMilliseconds < 400) return;
                    lastEvent = DateTime.Now;
                    Emit(new
                    {
                        type = "progress",
                        round = p.Round,
                        t = Math.Round(s.ElapsedSeconds, 1),
                        nodes = s.NodesExplored,
                        incumbent = Finite(s.Incumbent),
                        bound = Finite(s.Bound),
                        gap = Finite(s.Gap),
                        cols = s.Paths + s.Strings,
                        cuts = s.Cuts,
                        phase = $"r{p.Round} {s.Phase}",
                    });
                }
                else
                    Emit(new { type = "design-phase", round = p.Round, phase = p.Phase });
            };
            var res = designer.Run(job.Cancel.Token);
            if (res.Best.Best is not null)
            {
                var design = SolutionJson.DesignReport(res);
                job.Result = SolutionJson.Build(res.BestInstance, res.Best, design);
                SolutionJson.Save(res.BestInstance, res.Best, Path.Combine(ResultsDir(),
                    res.BestInstance.Name.Replace("+prop", "") + "+design"
                    + (req.Maintenance ? "-mnt" : "") + ".solution.json"), design);
                job.Status = "done";
            }
            else
            {
                job.Status = "failed";
                job.Error = $"no solution found ({res.StopReason})";
            }
        }
        catch (Exception ex)
        {
            job.Status = "failed";
            job.Error = ex.Message;
        }
        Emit(new { type = "status", status = job.Status, error = job.Error });
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
                MipHeuristicTimeLimit = Math.Max(20, job.Request.TimeLimitSeconds * 0.3),
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
            // "split" mode: polish the schedule with the geographic block cycle (regions,
            // then cross-region pairs), same budget again, monotone merges only
            if (job.Request.Regional && !job.Request.Maintenance && res.Best is not null
                && !job.Cancel.IsCancellationRequested)
            {
                var ro = new RegionalOptimizer(inst, new RegionalOptions
                {
                    TotalTimeLimitSeconds = job.Request.TimeLimitSeconds,
                    BlockTimeLimitSeconds = Math.Max(60, job.Request.TimeLimitSeconds / 4),
                    GapTarget = job.Request.GapTarget,
                });
                ro.Progress += msg =>
                {
                    var e = new { type = "design-phase", round = 0, phase = msg };
                    job.Events.Enqueue(e);
                    lock (job.EventLog) job.EventLog.Add(e);
                };
                var (polished, pProfit, _) = ro.Run(res.Best, job.Cancel.Token);
                if (pProfit > res.Objective + 1e-6)
                    res = res with
                    {
                        Best = polished, Objective = pProfit,
                        Gap = Math.Max(0, res.Bound - pProfit)
                            / Math.Max(1e-9, Math.Abs(pProfit)),
                    };
            }
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
