using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
namespace WinDiagnostics;

public static class Tool
{
    public const string Name = "win-process-pulse";
    public static readonly string[] ValueOptions = ["--interval-ms", "--top", "--memory-mib", "--cpu-percent"];
    public static readonly string[] Flags = [];
    public const string Help = "win-process-pulse [--interval-ms 1000] [--top 10] [--memory-mib 1024] [--cpu-percent 25] [--json]\nSamples local processes twice. CPU is percent of total logical processor capacity. Memory is the second sample's working set/private bytes. No command lines, paths, owners, or process control.";
    public static void ValidateOptions(Options o)
    {
        o.Number("--interval-ms", 1000, 100, 10000);
        o.Number("--top", 10, 1, 100);
        o.Number("--memory-mib", 1024, 1, 1048576);
        o.Number("--cpu-percent", 25, 1, 100);
    }
    public static double? CpuPercent(double before, double after, double elapsedMs, int processors)
    {
        if (!double.IsFinite(before) || !double.IsFinite(after) || !double.IsFinite(elapsedMs) ||
            before < 0 || after < before || elapsedMs <= 0 || processors < 1) return null;
        var value = (after - before) / elapsedMs / processors * 100;
        // A value beyond capacity indicates inconsistent timing/counters; do not hide it by clamping.
        return value <= 100.5 ? Math.Round(Math.Min(value, 100), 2) : null;
    }
    private sealed record Sample(int Pid, string Name, long StartTicks, double CpuMs, long WorkingSet, long PrivateBytes, long Timestamp);
    private static Dictionary<int, Sample> SampleProcesses(JsonArray issues, CancellationToken ct)
    {
        var result = new Dictionary<int, Sample>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var p in processes)
            {
                ct.ThrowIfCancellationRequested();
                if (result.Count >= 4096)
                {
                    issues.Add(new JsonObject { ["area"] = "limit", ["message"] = "4096-process evidence cap reached; remaining processes omitted." });
                    break;
                }
                if (p.Id == 0) continue; // Idle pseudo-process is not application CPU usage.
                try
                {
                    p.Refresh();
                    var name = p.ProcessName;
                    var start = p.StartTime.ToUniversalTime().Ticks;
                    var cpu = p.TotalProcessorTime.TotalMilliseconds;
                    var timestamp = Stopwatch.GetTimestamp();
                    result[p.Id] = new(p.Id, name, start, cpu, p.WorkingSet64, p.PrivateMemorySize64, timestamp);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    if (issues.Count < 50) issues.Add(new JsonObject { ["area"] = "process", ["pid"] = p.Id, ["message"] = "Process exited or its counters were inaccessible: " + ex.Message });
                }
            }
        }
        finally { foreach (var p in processes) p.Dispose(); }
        return result;
    }
    public static async Task<Collection> Collect(Options o, CancellationToken ct)
    {
        var issues = new JsonArray();
        var first = SampleProcesses(issues, ct);
        await Task.Delay(o.Number("--interval-ms", 1000, 100, 10000), ct);
        var second = SampleProcesses(issues, ct);
        var rows = new JsonArray();
        var processors = Environment.ProcessorCount;
        int matched = 0;
        foreach (var (pid, last) in second.OrderBy(x => x.Key))
        {
            double? cpu = null, elapsed = null;
            if (first.TryGetValue(pid, out var before) && before.StartTicks == last.StartTicks)
            {
                matched++;
                elapsed = Stopwatch.GetElapsedTime(before.Timestamp, last.Timestamp).TotalMilliseconds;
                cpu = CpuPercent(before.CpuMs, last.CpuMs, elapsed.Value, processors);
                if (cpu is null && issues.Count < 50) issues.Add(new JsonObject { ["area"] = "timing", ["pid"] = pid, ["message"] = "Inconsistent CPU counters; percentage omitted." });
            }
            rows.Add(new JsonObject { ["pid"] = pid, ["name"] = last.Name, ["cpuPercent"] = cpu,
                ["sampleMs"] = elapsed, ["workingSetBytes"] = last.WorkingSet, ["privateBytes"] = last.PrivateBytes });
        }
        return new(new JsonObject { ["processorCount"] = processors, ["intervalMs"] = o.Number("--interval-ms", 1000, 100, 10000),
            ["top"] = o.Number("--top", 10, 1, 100), ["memoryThresholdMiB"] = o.Number("--memory-mib", 1024, 1, 1048576),
            ["cpuThresholdPercent"] = o.Number("--cpu-percent", 25, 1, 100), ["unmatchedFirstSample"] = first.Count - matched,
            ["processes"] = rows }, issues);
    }
    public static void ValidateEvidence(JsonObject e)
    {
        V.Int(e, "processorCount", 1, 65536); V.Int(e, "intervalMs", 100, 10000); V.Int(e, "top", 1, 100);
        V.Int(e, "memoryThresholdMiB", 1, 1048576); V.Int(e, "cpuThresholdPercent", 1, 100);
        V.Int(e, "unmatchedFirstSample", 0, 4096); V.Rows(e, "processes", 4096);
        var ids = new HashSet<long>();
        foreach (var row in J.Rows(e, "processes"))
        {
            var id = V.Int(row, "pid", 1, int.MaxValue);
            if (!ids.Add(id)) throw new ArgumentException("Duplicate process PID.");
            V.Text(row, "name"); V.Int(row, "workingSetBytes", 0, long.MaxValue); V.Int(row, "privateBytes", 0, long.MaxValue);
            V.Real(row, "cpuPercent", 0, 100, true); V.Real(row, "sampleMs", double.Epsilon, double.MaxValue, true);
            if ((row["cpuPercent"] is null) != (row["sampleMs"] is null) && row["sampleMs"] is null)
                throw new ArgumentException("CPU percentage requires sampleMs.");
        }
    }
    public static List<Finding> Analyze(JsonObject e)
    {
        var result = new List<Finding>();
        var rows = J.Rows(e, "processes").ToArray();
        foreach (var row in rows.OrderByDescending(x => J.Number(x, "cpuPercent")).Take((int)J.Number(e, "top")))
            if (row["cpuPercent"] is not null && J.Number(row, "cpuPercent") >= J.Number(e, "cpuThresholdPercent"))
                result.Add(new("cpu-threshold", "warning", "Process met the CPU review threshold", $"{J.Text(row, "name")} (PID {J.Text(row, "pid")}): {J.Number(row, "cpuPercent"):F2}% of total logical capacity during this sample.", "Repeat with a longer interval and correlate with the symptom; one sample does not establish a runaway process."));
        foreach (var row in rows.OrderByDescending(x => J.Number(x, "workingSetBytes")).Take((int)J.Number(e, "top")))
            if (J.Number(row, "workingSetBytes") >= J.Number(e, "memoryThresholdMiB") * 1048576)
                result.Add(new("memory-threshold", "info", "Process met the working-set review threshold", $"{J.Text(row, "name")} (PID {J.Text(row, "pid")}): {J.Bytes(J.Number(row, "workingSetBytes"))} resident.", "Compare repeated samples and expected workload. Working sets can include shared pages; do not sum them as unique RAM or infer a leak."));
        if (rows.Any(x => x["cpuPercent"] is null)) result.Add(new("cpu-unavailable", "info", "Some processes have no comparable CPU sample", "New processes, PID reuse, or inconsistent counters can leave CPU blank.", "Repeat the sample; blank CPU is not zero usage."));
        if (rows.Length == 0) result.Add(new("no-process-evidence", "warning", "No process counters available", "No accessible processes were captured.", "Review collection issues and permissions."));
        return result;
    }
    public static IEnumerable<string> Describe(JsonObject e)
    {
        yield return $"{J.Text(e, "processorCount")} logical processors; requested interval {J.Text(e, "intervalMs")} ms; {J.Rows(e, "processes").Count()} accessible processes; {J.Text(e, "unmatchedFirstSample")} first-sample processes not matched.";
        yield return "Top CPU (total logical capacity):";
        foreach (var row in J.Rows(e, "processes").OrderByDescending(x => J.Number(x, "cpuPercent", -1)).Take((int)J.Number(e, "top")))
            yield return $"  {J.Text(row, "pid"),7} {J.Text(row, "name")} CPU {(row["cpuPercent"] is null ? "n/a" : J.Text(row, "cpuPercent") + "%")} | working set {J.Bytes(J.Number(row, "workingSetBytes"))}";
        yield return "Top working set:";
        foreach (var row in J.Rows(e, "processes").OrderByDescending(x => J.Number(x, "workingSetBytes")).Take((int)J.Number(e, "top")))
            yield return $"  {J.Text(row, "pid"),7} {J.Text(row, "name")} | resident {J.Bytes(J.Number(row, "workingSetBytes"))} | private {J.Bytes(J.Number(row, "privateBytes"))}";
    }
    public static string[] SmokeArgs => ["--interval-ms", "100"];
}
