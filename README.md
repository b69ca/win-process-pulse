# win-process-pulse

Sample process CPU and memory without terminating or inspecting command lines.

Read-only C# / .NET 10 troubleshooting CLI for Windows 11 and Windows Server
2022/2025. No third-party runtime packages. Text and structured JSON output use
the same evidence and findings. Saved JSON can be re-analyzed offline on any
platform supported by .NET 10.

## Run

Download `win-process-pulse-win-x64` from a successful [GitHub Actions run](https://github.com/b69ca/win-process-pulse/actions),
extract it, and run `win-process-pulse.exe` in a terminal. The self-contained executable
includes the runtime. Initial binaries are unsigned. Build from source if preferred:

```powershell
dotnet run --project WinProcessPulse.csproj -c Release -- --help
dotnet run --project WinProcessPulse.csproj -c Release -- --json > report.json
dotnet run --project WinProcessPulse.csproj -c Release -- --input report.json
```

## Common interface

- `--json`: complete evidence report on stdout, including collection issues and findings.
- `--input FILE`: replay this tool's schemaVersion 1 JSON report without live queries.
  Cannot be combined with live collection options. Maximum input size: 8 MiB.
- `--help`: usage and exit-code reference.

Exit codes: 0 = report produced (including warnings or partial collection);
2 = invalid usage/snapshot; 3 = collection failed; 4 = live collection requires
Windows; 130 = cancelled. Findings do not change exit status. Check JSON `status`
and `issues` before treating a report as complete. Invalid usage is written to
stderr even with `--json`; collection failures can produce a failed JSON envelope.

Snapshots include schemaVersion, tool, version, collection/analysis times,
platform, mode, status, evidence, issues, and findings. Offline analysis preserves
the collection timestamp and recomputes findings. Editing snapshots changes the
evidence; these files are not cryptographically authenticated.

## Options and interpretation

- `--interval-ms N`: requested pause between sweeps, 100–10000 ms; default 1000.
- `--top N`: show up to N entries in each CPU and memory ranking, 1–100; default 10.
  Threshold findings are also bounded to these rankings; JSON retains all captured rows.
- `--memory-mib N`: working-set review threshold, 1–1048576 MiB; default 1024.
- `--cpu-percent N`: CPU review threshold, 1–100; default 25.

CPU is the change in TotalProcessorTime divided by each process's actual monotonic
sample interval and Environment.ProcessorCount, expressed as a percentage of
logical capacity visible to this process. On a four-logical-CPU system, one busy
thread is about 25%. It may differ from Task Manager or host-wide capacity under
CPU affinity, processor groups, containers, or resource limits. Matching uses PID
and process start time; new or reused PIDs have unknown CPU. Counter regressions
or percentages beyond capacity are left unknown. The requested pause is not the
exact interval: sweep time contributes to each process's measured interval.

Working set is resident memory; private bytes represent private committed memory,
which need not all be resident. Shared pages can appear in several working sets.
A threshold is a review prompt, not proof of a leak or faulty process. Missing
permissions and exited processes are reported as partial evidence; PID 0 is skipped.
The evidence cap is 4096 accessible processes per sweep. These are snapshots, not
continuous monitoring, and do not track processes that start and exit between sweeps.

```powershell
.\win-process-pulse.exe --interval-ms 3000 --top 15 --cpu-percent 20
.\win-process-pulse.exe --memory-mib 2048 --json > pulse.json
```

Technical references: [TotalProcessorTime](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.totalprocessortime?view=net-10.0),
[WorkingSet64](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.workingset64?view=net-10.0),
[Environment.ProcessorCount](https://learn.microsoft.com/en-us/dotnet/api/system.environment.processorcount?view=net-10.0).

## Privacy and troubleshooting

No configuration changes, remediation, telemetry, or external network calls.
Reports stay on stdout unless you redirect them. Reports can reveal process,
network, or task names, addresses, and system configuration. Review and redact
before sharing. Synthetic test fixtures contain no private host data.

Run initially without elevation. Permission failures remain explicit; rerun with
appropriate privileges only if needed. No matching rule does not prove a healthy
system. Read the scope and limitations above before interpreting a finding.
Ctrl+C cancels collection where the underlying API permits it. Synchronous OS
API calls may delay cancellation. Remove the downloaded folder to uninstall.

## Build and test

Requires the .NET 10 SDK (global.json permits the latest installed 10.0 feature band).

```sh
dotnet build WinProcessPulse.csproj -c Release
dotnet run --project tests/Checks.csproj -c Release
dotnet run --project WinProcessPulse.csproj -c Release -- --input tests/fixture.json --json
dotnet publish WinProcessPulse.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/win-x64
```

The dependency-free test executable exercises diagnostic decisions, boundaries,
malformed evidence, option validation, and CLI replay. CI runs on Ubuntu and
Windows; Windows adds live read-only collector smoke checks, PowerShell syntax
validation when applicable, standalone publishing, and executable replay. A live
smoke test establishes API compatibility, not coverage of every real-world failure.

## License

MIT — see [LICENSE](LICENSE).
