using System.Text.Json.Nodes;
using WinDiagnostics;
int passed = 0, failed = 0;
void Assert(bool value, string why) { if (!value) throw new Exception(why); }
void Reject(Action body) { try { body(); } catch (ArgumentException) { return; } throw new Exception("Expected invalid evidence/options rejection"); }
void Check(string name, Action body) { try { body(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + e.Message); } }
JsonObject Snapshot() => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixture.json")))!;
JsonObject Evidence() => (JsonObject)Snapshot()["evidence"]!.DeepClone();
bool Has(JsonObject e, string id) => Tool.Analyze(e).Any(f => f.Id == id);
Check("snapshot replay retains evidence and rejects wrong tool/schema", () => {
    var s = Snapshot(); var c = Core.ReadSnapshot(s); Assert(c.Issues.Count == 0, "fixture issues");
    c.Evidence["testOnly"] = true; Assert(s["evidence"]!["testOnly"] is null, "clone isolation");
    s["tool"] = "other"; Reject(() => Core.ReadSnapshot(s)); s = Snapshot(); s["schemaVersion"] = 2; Reject(() => Core.ReadSnapshot(s));
    s = Snapshot(); s["issues"] = null; Reject(() => Core.ReadSnapshot(s));
});
Check("strict CLI and offline/live option separation", () => {
    Reject(() => Options.Parse(["--unknown"], Tool.ValueOptions, Tool.Flags));
    Reject(() => Options.Parse(["--input"], Tool.ValueOptions, Tool.Flags));
    Reject(() => Options.Parse(["--json", "--json"], Tool.ValueOptions, Tool.Flags));
    Reject(() => Options.Parse(["--input", "f", Tool.ValueOptions[0], "x"], Tool.ValueOptions, Tool.Flags));
    Tool.ValidateOptions(Options.Parse([], Tool.ValueOptions, Tool.Flags));
});
Check("malformed rows are rejected instead of silently analyzed", () => {
    Reject(() => Tool.ValidateEvidence(new JsonObject()));
    var e = Evidence(); e["processes"] = new JsonArray { 7 }; Reject(() => Tool.ValidateEvidence(e));
});

Check("CPU calculation normalizes logical capacity", () => { Assert(Tool.CpuPercent(100, 1100, 1000, 4) == 25, "normalization"); Assert(Tool.CpuPercent(100, 100, 500, 1) == 0, "idle delta"); });
Check("invalid timing and counter regressions leave CPU unknown", () => { Assert(Tool.CpuPercent(100, 99, 1000, 4) is null, "counter rollback"); Assert(Tool.CpuPercent(0, 5000, 1000, 1) is null, "beyond capacity"); Assert(Tool.CpuPercent(0, 1, 0, 1) is null, "zero duration"); Assert(Tool.CpuPercent(0, 1, 1, 0) is null, "zero CPUs"); Assert(Tool.CpuPercent(0, double.NaN, 1, 1) is null, "nonfinite"); });
Check("threshold findings and unknown CPU", () => { var e=Evidence(); Assert(Has(e,"cpu-threshold") && Has(e,"memory-threshold") && Has(e,"cpu-unavailable"),"findings"); });
Check("exact thresholds are inclusive", () => { var e=Evidence(); e["processes"]![0]!["cpuPercent"]=25; e["processes"]![0]!["workingSetBytes"]=1073741824L; Assert(Has(e,"cpu-threshold")&&Has(e,"memory-threshold"),"boundary"); });
Check("below threshold and null CPU never imply high CPU", () => { var e=Evidence(); e["processes"]![0]!["cpuPercent"]=null; e["processes"]![0]!["workingSetBytes"]=1; Assert(!Has(e,"cpu-threshold")&&!Has(e,"memory-threshold"),"false positive"); });
Check("empty collection is explicit", () => { var e=Evidence(); e["processes"]=new JsonArray(); Assert(Has(e,"no-process-evidence"),"empty"); });
Check("duplicate PID and negative counters are rejected", () => { var e=Evidence(); ((JsonArray)e["processes"]!).Add(e["processes"]![0]!.DeepClone()); Reject(()=>Tool.ValidateEvidence(e)); e=Evidence(); e["processes"]![0]!["workingSetBytes"]=-1; Reject(()=>Tool.ValidateEvidence(e)); });
Check("sample duration required for percentage", () => { var e=Evidence(); e["processes"]![0]!["sampleMs"]=null; Reject(()=>Tool.ValidateEvidence(e)); });
Check("CLI bounds and integer parsing", () => { foreach(var args in new[]{new[]{"--interval-ms","99"},new[]{"--top","0"},new[]{"--cpu-percent","101"},new[]{"--memory-mib","-1"}}) Reject(()=>Tool.ValidateOptions(Options.Parse(args,Tool.ValueOptions,Tool.Flags))); });

var capture = new StringWriter(); var oldOut = Console.Out; var oldErr = Console.Error;
try {
    Console.SetOut(capture); Console.SetError(capture);
    var code = await Core.Run(["--input", Path.Combine(AppContext.BaseDirectory, "fixture.json"), "--json"]);
    Console.SetOut(oldOut); Console.SetError(oldErr);
    Check("CLI JSON offline round-trip", () => { Assert(code == 0, "exit code"); var report = (JsonObject)JsonNode.Parse(capture.ToString())!; Assert(J.Text(report, "mode") == "offline", "mode"); Core.ReadSnapshot(report); });
} finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
if (OperatingSystem.IsWindows()) {
    try {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(110));
        var options = Options.Parse(Tool.SmokeArgs, Tool.ValueOptions, Tool.Flags);
        Tool.ValidateOptions(options); var live = await Tool.Collect(options, timeout.Token); Tool.ValidateEvidence(live.Evidence);
        Assert(live.Evidence["processes"] is JsonArray, "missing live rows");
        Assert(((JsonArray)live.Evidence["processes"]!).Count > 0, "expected live evidence");
        passed++; Console.WriteLine($"PASS Windows live collection ({((JsonArray)live.Evidence["processes"]!).Count} rows, {live.Issues.Count} issues)");
    } catch (Exception e) { failed++; Console.Error.WriteLine("FAIL Windows live collection: " + e); }
}
Console.WriteLine($"{passed} passed; {failed} failed"); return failed == 0 ? 0 : 1;
