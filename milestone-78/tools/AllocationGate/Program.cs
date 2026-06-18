// AllocationGate — enforcement tool for the FablePool "no allocations after warmup" contract.
//
// Attaches to (or launches) a .NET process, opens an EventPipe session subscribed to the
// CLR GC provider, and counts GCAllocationTick events observed AFTER the warmup boundary.
// The warmup boundary is detected either by a custom EventSource marker event emitted by
// the target ("FablePool-Contract"/"WarmupComplete" by default) or, as a fallback, by a
// wall-clock warmup timeout. Any non-baselined allocation tick observed in the measurement
// window fails the gate (non-zero exit code), which makes the tool directly usable as a
// CI gate. See tools/AllocationGate/README.md and docs/design/08-no-allocation-contract.md.
//
// Caveat (documented in the README): GCAllocationTick is a *sampled* event — the runtime
// raises it roughly once per ~100 KB of allocation per type. A zero-tick measurement window
// therefore proves "no allocation burst >= sampling threshold" rather than literally zero
// bytes; run long windows (and soak tests) so that any steady allocation leak crosses the
// threshold and is caught. .NET 9+ adds a per-object AllocationSampled event that can
// tighten this further; this tool is written against the long-stable GCAllocationTick so
// it works on every supported runtime.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace FablePool.Tools.AllocationGate;

internal sealed class Options
{
    public int Pid;
    public List<string> LaunchCommand = new();
    public string MarkerProvider = "FablePool-Contract";
    public string MarkerEvent = "WarmupComplete";
    public double WarmupSeconds = 60;
    public double MeasureSeconds = 30;
    public long MaxTicks = 0;
    public string? BaselinePath;
    public string? JsonReportPath;
    public bool KillOnExit = true;
    public bool Verbose;
}

internal sealed class TypeStats
{
    public long Ticks;
    public long ApproxBytes;
    public readonly HashSet<int> ThreadIds = new();
}

internal sealed class GateState
{
    public volatile bool Measuring;
    public DateTime MeasureStartUtc;
    public string Trigger = "none";
}

internal static class Program
{
    private static int Main(string[] args)
    {
        Options opts;
        try
        {
            opts = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }

        Process? launched = null;
        int pid = opts.Pid;

        try
        {
            if (opts.LaunchCommand.Count > 0)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = opts.LaunchCommand[0],
                    UseShellExecute = false,
                };
                for (int i = 1; i < opts.LaunchCommand.Count; i++)
                    psi.ArgumentList.Add(opts.LaunchCommand[i]);

                launched = Process.Start(psi)
                           ?? throw new InvalidOperationException("failed to launch target process");
                pid = launched.Id;
                Console.WriteLine($"[gate] launched target pid={pid}: {string.Join(' ', opts.LaunchCommand)}");
            }
            else
            {
                Console.WriteLine($"[gate] attaching to pid={pid}");
            }

            return Run(opts, pid, launched);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[gate] fatal: " + ex.Message);
            if (opts.Verbose) Console.Error.WriteLine(ex);
            return 2;
        }
        finally
        {
            if (launched is not null && opts.KillOnExit)
            {
                try
                {
                    if (!launched.HasExited)
                    {
                        Console.WriteLine("[gate] terminating launched target");
                        launched.Kill(entireProcessTree: true);
                    }
                    launched.WaitForExit(5000);
                }
                catch { /* best effort */ }
            }
        }
    }

    private static int Run(Options opts, int pid, Process? launched)
    {
        var baseline = LoadBaseline(opts.BaselinePath);
        var client = new DiagnosticsClient(pid);

        var providers = new List<EventPipeProvider>
        {
            // CLR GC keyword (0x1) at Verbose — GCAllocationTick is a verbose-level event.
            new EventPipeProvider(
                "Microsoft-Windows-DotNETRuntime",
                System.Diagnostics.Tracing.EventLevel.Verbose,
                (long)ClrTraceEventParser.Keywords.GC),
            // The contract marker EventSource in the target process.
            new EventPipeProvider(
                opts.MarkerProvider,
                System.Diagnostics.Tracing.EventLevel.Informational,
                keywords: -1),
        };

        EventPipeSession? session = null;
        Exception? lastConnectError = null;
        // The diagnostics IPC channel may not be up yet immediately after launch; retry briefly.
        for (int attempt = 0; attempt < 25 && session is null; attempt++)
        {
            try
            {
                session = client.StartEventPipeSession(providers, requestRundown: false, circularBufferMB: 64);
            }
            catch (Exception ex)
            {
                lastConnectError = ex;
                Thread.Sleep(200);
            }
        }
        if (session is null)
            throw new InvalidOperationException(
                $"could not open EventPipe session on pid {pid}: {lastConnectError?.Message}");

        using (session)
        {
            var state = new GateState();
            var stats = new Dictionary<string, TypeStats>(StringComparer.Ordinal);
            long baselineTicks = 0;
            long baselineBytes = 0;
            long preWarmupTicks = 0;

            var source = new EventPipeEventSource(session.EventStream);

            source.Clr.GCAllocationTick += data =>
            {
                if (!state.Measuring)
                {
                    preWarmupTicks++;
                    return;
                }
                var typeName = string.IsNullOrEmpty(data.TypeName) ? "<unknown>" : data.TypeName;
                if (baseline.Contains(typeName))
                {
                    baselineTicks++;
                    baselineBytes += data.AllocationAmount64;
                    return;
                }
                if (!stats.TryGetValue(typeName, out var s))
                {
                    s = new TypeStats();
                    stats[typeName] = s;
                }
                s.Ticks++;
                s.ApproxBytes += data.AllocationAmount64;
                s.ThreadIds.Add(data.ThreadID);
                if (opts.Verbose)
                {
                    Console.WriteLine(
                        $"[gate] ALLOC tick: type={typeName} approxBytes={data.AllocationAmount64} thread={data.ThreadID}");
                }
            };

            source.Dynamic.All += data =>
            {
                if (state.Measuring) return;
                if (string.Equals(data.ProviderName, opts.MarkerProvider, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(data.EventName, opts.MarkerEvent, StringComparison.OrdinalIgnoreCase))
                {
                    state.MeasureStartUtc = DateTime.UtcNow;
                    state.Trigger = "marker";
                    state.Measuring = true;
                    Console.WriteLine(
                        $"[gate] warmup marker '{opts.MarkerProvider}/{opts.MarkerEvent}' observed; measurement window open " +
                        $"({opts.MeasureSeconds:0.#}s)");
                }
            };

            bool targetExitedEarly = false;

            var control = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                // Phase 1: wait for the marker (or warmup timeout fallback).
                while (!state.Measuring && sw.Elapsed.TotalSeconds < opts.WarmupSeconds)
                {
                    if (launched is { HasExited: true }) { targetExitedEarly = true; break; }
                    await Task.Delay(100).ConfigureAwait(false);
                }
                if (!targetExitedEarly && !state.Measuring)
                {
                    state.MeasureStartUtc = DateTime.UtcNow;
                    state.Trigger = "warmup-timeout";
                    state.Measuring = true;
                    Console.WriteLine(
                        $"[gate] no warmup marker within {opts.WarmupSeconds:0.#}s; opening measurement window on timeout " +
                        $"({opts.MeasureSeconds:0.#}s)");
                }
                // Phase 2: measurement window.
                if (!targetExitedEarly)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(opts.MeasureSeconds);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (launched is { HasExited: true }) { targetExitedEarly = true; break; }
                        await Task.Delay(100).ConfigureAwait(false);
                    }
                }
                try { session.Stop(); } catch { /* stopping a closing session is fine */ }
            });

            try
            {
                source.Process(); // blocks until session.Stop() closes the stream
            }
            catch (Exception ex)
            {
                // The stream commonly faults when stopped or when the target exits; that is expected.
                if (opts.Verbose) Console.WriteLine("[gate] event stream ended: " + ex.Message);
            }
            control.Wait();

            return Report(opts, pid, state, stats, baselineTicks, baselineBytes, preWarmupTicks, targetExitedEarly);
        }
    }

    private static int Report(
        Options opts, int pid, GateState state, Dictionary<string, TypeStats> stats,
        long baselineTicks, long baselineBytes, long preWarmupTicks, bool targetExitedEarly)
    {
        long violationTicks = 0;
        long violationBytes = 0;
        foreach (var s in stats.Values) { violationTicks += s.Ticks; violationBytes += s.ApproxBytes; }

        bool measured = state.Measuring;
        bool pass = measured && !targetExitedEarly && violationTicks <= opts.MaxTicks;

        Console.WriteLine();
        Console.WriteLine("================ Allocation Gate Report ================");
        Console.WriteLine($"  target pid:           {pid}");
        Console.WriteLine($"  measurement trigger:  {state.Trigger}");
        Console.WriteLine($"  measurement start:    {(measured ? state.MeasureStartUtc.ToString("O") : "<never opened>")}");
        Console.WriteLine($"  pre-warmup ticks:     {preWarmupTicks} (informational, not gated)");
        Console.WriteLine($"  baselined ticks:      {baselineTicks} (~{baselineBytes} bytes, allowed)");
        Console.WriteLine($"  violation ticks:      {violationTicks} (~{violationBytes} bytes) — budget {opts.MaxTicks}");
        if (targetExitedEarly)
            Console.WriteLine("  WARNING: target process exited before the measurement window completed.");

        if (stats.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Violating allocation types (ticks desc):");
            Console.WriteLine("  ----------------------------------------------------");
            foreach (var kv in stats.OrderByDescending(k => k.Value.Ticks).Take(50))
            {
                Console.WriteLine(
                    $"  {kv.Value.Ticks,8}  ~{kv.Value.ApproxBytes,12} B  threads[{string.Join(',', kv.Value.ThreadIds)}]  {kv.Key}");
            }
            if (stats.Count > 50)
                Console.WriteLine($"  ... and {stats.Count - 50} more types (see JSON report).");
        }

        Console.WriteLine();
        Console.WriteLine(pass ? "  RESULT: PASS — no-allocation contract upheld."
                               : "  RESULT: FAIL — allocations detected after warmup (or window never completed).");
        Console.WriteLine("=========================================================");

        if (opts.JsonReportPath is not null)
        {
            var report = new
            {
                pid,
                trigger = state.Trigger,
                measureStartUtc = measured ? state.MeasureStartUtc : (DateTime?)null,
                measureSeconds = opts.MeasureSeconds,
                maxTicksBudget = opts.MaxTicks,
                preWarmupTicks,
                baselineTicks,
                baselineApproxBytes = baselineBytes,
                violationTicks,
                violationApproxBytes = violationBytes,
                targetExitedEarly,
                pass,
                types = stats
                    .OrderByDescending(k => k.Value.Ticks)
                    .Select(k => new
                    {
                        type = k.Key,
                        ticks = k.Value.Ticks,
                        approxBytes = k.Value.ApproxBytes,
                        threadIds = k.Value.ThreadIds.OrderBy(t => t).ToArray(),
                    })
                    .ToArray(),
            };
            File.WriteAllText(
                opts.JsonReportPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"[gate] JSON report written to {opts.JsonReportPath}");
        }

        return pass ? 0 : 1;
    }

    private static HashSet<string> LoadBaseline(string? path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (path is null) return set;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            set.Add(line);
        }
        Console.WriteLine($"[gate] loaded {set.Count} baselined type name(s) from {path}");
        return set;
    }

    private static Options ParseArgs(string[] args)
    {
        var opts = new Options();
        int dashDash = Array.IndexOf(args, "--");
        string[] optionArgs = dashDash >= 0 ? args[..dashDash] : args;
        if (dashDash >= 0)
            opts.LaunchCommand.AddRange(args[(dashDash + 1)..]);

        for (int i = 0; i < optionArgs.Length; i++)
        {
            string a = optionArgs[i];
            string Next()
            {
                if (i + 1 >= optionArgs.Length) throw new ArgumentException($"missing value for {a}");
                return optionArgs[++i];
            }
            switch (a)
            {
                case "--pid": opts.Pid = int.Parse(Next()); break;
                case "--marker-provider": opts.MarkerProvider = Next(); break;
                case "--marker-event": opts.MarkerEvent = Next(); break;
                case "--warmup-seconds": opts.WarmupSeconds = double.Parse(Next()); break;
                case "--measure-seconds": opts.MeasureSeconds = double.Parse(Next()); break;
                case "--max-ticks": opts.MaxTicks = long.Parse(Next()); break;
                case "--baseline": opts.BaselinePath = Next(); break;
                case "--json": opts.JsonReportPath = Next(); break;
                case "--no-kill": opts.KillOnExit = false; break;
                case "--verbose": opts.Verbose = true; break;
                case "--help" or "-h": PrintUsage(); Environment.Exit(0); break;
                default: throw new ArgumentException($"unknown option '{a}'");
            }
        }

        if (opts.Pid == 0 && opts.LaunchCommand.Count == 0)
            throw new ArgumentException("specify --pid <n> or a launch command after '--'");
        if (opts.Pid != 0 && opts.LaunchCommand.Count > 0)
            throw new ArgumentException("--pid and a launch command are mutually exclusive");
        if (opts.WarmupSeconds <= 0 || opts.MeasureSeconds <= 0)
            throw new ArgumentException("--warmup-seconds and --measure-seconds must be positive");
        return opts;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            AllocationGate — CI gate for the 'no allocations after warmup' contract.

            Usage:
              AllocationGate [options] --pid <pid>
              AllocationGate [options] -- <command> [args...]

            Options:
              --pid <n>                Attach to an already-running process.
              --marker-provider <name> EventSource name signalling warmup completion
                                       (default: FablePool-Contract).
              --marker-event <name>    Event name for the warmup marker (default: WarmupComplete).
              --warmup-seconds <s>     Max time to wait for the marker before opening the
                                       measurement window anyway (default: 60).
              --measure-seconds <s>    Length of the gated measurement window (default: 30).
              --max-ticks <n>          Allocation-tick budget inside the window (default: 0).
              --baseline <file>        File of type names (one per line, '#' comments) whose
                                       allocations are tolerated (e.g. known cold-path types).
              --json <file>            Write a machine-readable JSON report.
              --no-kill                Do not kill a launched target on exit.
              --verbose                Log every violating allocation tick as it arrives.

            Exit codes: 0 = pass, 1 = contract violation / incomplete window, 2 = usage or runtime error.
            """);
    }
}
