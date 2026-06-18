# Design Doc 07 — Threading Model and Core Pinning

Status: Final draft for review
Depends on: 05-ring-buffers.md, 06-unmanaged-arenas.md
Audience: Core engine engineers; SRE/deployment owners

---

## 1. Principles

1. **One thread per hot role, pinned to a dedicated isolated core.** No thread
   pool, no `Task`, no `async/await` on the hot path. Hot threads are created at
   startup and run a poll loop until shutdown.
2. **Threads communicate only via rings** (Doc 05). No locks, no shared mutable
   state outside ring slots and explicitly single-writer telemetry counters.
3. **Hot threads never block**: no syscalls in steady state except the network
   send/recv on the designated I/O threads (and those use busy-polled non-blocking
   sockets or kernel-bypass where available).
4. **Cold work lives on unpinned cores** under the normal .NET thread pool and may
   allocate freely.

## 2. Thread roster (reference deployment, one venue)

| Thread | Role | Core | Loop |
|---|---|---|---|
| `feed-0` | NIC recv → decode → publish `MarketDataEvent` | 2 | busy-poll socket → `BroadcastRing.TryWrite` |
| `strategy-0..k` | consume MD, decide, emit `OrderCommand` | 3..3+k | `RingReader.Drain` → logic → `MpscRing.TryWrite` |
| `gateway-0` | consume `OrderCommand` → encode → NIC send; recv exec reports → publish `ExecReport` | 4+k | `MpscRing.Drain` + socket poll |
| `timer-0` | TSC-based timer wheel → timeout events into MD-style ring | shared with gateway or own core | wheel scan |
| `logger` | drain `LogRing`, render, write | unpinned (cold set) | batched drain, may block |
| `telemetry` | sample ring lags, counters, EventPipe session (Doc 08) | unpinned | 100 ms tick |
| `control` | admin commands, config, snapshots | unpinned | blocking queue |

Core 0–1 are left to the OS/IRQs (see §5). SMT siblings of hot cores are left idle
or assigned only to that core's paired role (never an unrelated noisy thread).

## 3. Topology diagram (textual)

```
                 ┌────────────────────────────────────────────────────┐
   NIC RX (MD)   │ feed-0  [core 2]                                   │
  ───────────────▶  decode → BroadcastRing<MarketDataEvent> (SPMC)    │
                 └───────────────┬───────────────────┬────────────────┘
                                 │ reader 0          │ reader k
                 ┌───────────────▼──────┐   ┌────────▼─────────────┐
                 │ strategy-0 [core 3]  │…  │ strategy-k [core 3+k]│
                 │ books (StaticArena)  │   │                      │
                 │ decide()             │   │                      │
                 └───────────┬──────────┘   └─────────┬────────────┘
                             │ MpscRing<OrderCommand> │
                             └───────────┬────────────┘
                 ┌───────────────────────▼───────────────────────────┐
   NIC TX (ord)  │ gateway-0 [core 4+k]                              │
  ◀──────────────│  encode → send;  recv → ExecReport                │
                 │  BroadcastRing<ExecReport> ───────────────────────┼──▶ strategies
                 └───────────────────────────────────────────────────┘
   EmergencyRing<OrderCommand> (SPSC per strategy → gateway, reserved capacity)
   LogRing (MPSC, all hot threads → logger [cold cores])
```

Latency-critical path: `NIC → feed-0 → strategy-i → gateway-0 → NIC` — three ring
hops, each one cache-line transfer; budget ≤ 1 µs software time at p99 excluding
wire/NIC (budgets per stage in `docs/design/00-overview.md` §5).

## 4. The poll loop (normative shape)

Every hot thread runs this exact skeleton; deviations require design review:

```csharp
[HotPath]
private void RunLoop(CancellationToken shutdown)   // token polled, never awaited
{
    PinAndConfigure();                  // §5: affinity, priority, name
    WarmupHandshake();                  // touch pages, JIT-warm all paths (Doc 08 §2)
    var spin = new SpinPolicy(_config);

    while (!Volatile.Read(ref _stopRequested))
    {
        int work = 0;
        work += PollPrimaryInput();     // e.g. RingReader.Drain(ref handler, 64)
        work += PollSecondaryInput();   // e.g. exec reports, timer ring
        _frameArena.Reset();            // end of frame: scratch rewound

        if (work == 0) spin.Idle();     // X86Base.Pause()-based; §7
        else           spin.NoteWork();

        _heartbeat = Stopwatch.GetTimestamp();  // single-writer telemetry
    }
}
```

Rules:

- **Bounded batches** (`maxBatch` 64): keeps worst-case frame time bounded so the
  FrameArena reset cadence and heartbeat stay regular.
- **No allocation, no locks, no blocking syscalls** in the loop body — enforced by
  Doc 08 machinery.
- **Heartbeat**: each hot thread publishes a timestamp every frame; the telemetry
  thread alarms if any heartbeat is stale > 10 ms (`HotThreadStalled`, Doc 10 §6.4).

## 5. OS and hardware configuration

This is part of the deliverable: the software design assumes this environment, and
startup *verifies* it (warn or refuse to start, per `StrictEnvironment` config).

### 5.1 Linux (primary production target)

- Kernel cmdline: `isolcpus=managed_irq,domain,2-15 nohz_full=2-15
  rcu_nocbs=2-15` (adjust range to hot cores); `idle=poll` optional per latency
  budget vs. power.
- IRQ affinity: NIC queues used by hot sockets steered to the feed/gateway cores'
  *adjacent* cores or handled via busy-polling (`SO_BUSY_POLL`) / kernel-bypass
  (Onload/VMA/DPDK-class stacks are out of scope for this doc but slot in at the
  transport interface, Doc 11 §4).
- `cpufreq` governor `performance`; C-states limited (`max_cstate=1`) on hot cores.
- Transparent huge pages: `madvise` mode (we request explicitly, Doc 06 §5).
- `numactl` not required: the process self-binds (§6).
- Startup verification: parse `/proc/cmdline`, `/sys/devices/system/cpu/*/cpufreq`,
  `/proc/interrupts` deltas during warmup (hot cores must show ~0 IRQs), and log a
  signed environment report.

### 5.2 Windows (supported, secondary)

- `SetThreadAffinityMask`/`SetThreadGroupAffinity`, `SetThreadPriority(TIME_CRITICAL)`
  for hot threads; `SetPriorityClass(HIGH_PRIORITY_CLASS)` (not REALTIME by default).
- Power plan: High performance; core parking disabled on hot cores.
- Verification mirrors Linux where APIs exist; otherwise documented manual checklist.

### 5.3 .NET runtime configuration (runtimeconfig)

```json
{
  "configProperties": {
    "System.GC.Server": false,
    "System.GC.Concurrent": true,
    "System.GC.CpuGroup": false,
    "System.GC.HeapHardLimit": 536870912,
    "System.Runtime.TieredCompilation": true,
    "System.Runtime.TieredPGO": true,
    "System.Runtime.ReadyToRun": false
  }
}
```

Rationale:

- **Workstation GC, background concurrent**: after warmup we allocate nothing on
  hot threads, so GCs are driven only by cold-side allocation; workstation +
  background keeps cold GCs from suspending with server-GC's per-core heap threads
  occupying our isolated cores. The GC's suspension still stops hot threads
  (GC is process-wide) — our defense is *frequency* (near-zero gen2) and *cause
  elimination*, not GC-mode magic; see Doc 08 §6 for the measured-budget gate.
- **Heap hard limit 512 MiB** keeps the managed heap small ⇒ short pauses when cold
  GCs do occur; bulk data is unmanaged (Doc 06).
- **TieredCompilation + TieredPGO on, R2R off**: we *want* full Tier-1/PGO code,
  and we force promotion during warmup (Doc 08 §2.3) rather than disabling tiering
  (which would leave everything at unoptimized-tier-equivalent? no — it compiles
  Tier1 directly but without PGO and with worse startup; measured worse for us).
- Hot threads call `Thread.BeginThreadAffinity()` (formal), set
  `IsBackground=false`, `Priority=Highest`, and use OS affinity via P/Invoke
  (`sched_setaffinity` / `SetThreadGroupAffinity`) rather than
  `Process.ProcessorAffinity` (process-wide is too blunt).

GC-thread containment: `GCHeapAffinitizeMask` is set (server-GC only — N/A here);
for workstation GC, background GC thread priority is left default and runs on cold
cores because hot cores are isolated from the scheduler (`isolcpus`) — *only*
explicitly affinitized threads land there, and the CLR's GC threads are not, which
is exactly what we want. Caveat verified at startup: suspension EE events (see
Doc 08 §6) measure the real impact.

## 6. NUMA placement

- One venue's full pipeline (feed, strategies, gateway, rings, books) lives on a
  **single NUMA node**, the node local to the NIC (PCIe locality from
  `/sys/class/net/<if>/device/numa_node`).
- `StaticArena` for that pipeline binds to that node (Doc 06 §5); FrameArenas are
  allocated by their owning pinned thread (first-touch ⇒ local).
- Multi-venue hosts replicate the pipeline per node rather than spanning nodes.
- Cross-node communication (e.g. cross-venue arbitrage signals) goes through a
  designated pair of SPSC rings whose slabs live on the *consumer's* node, with the
  measured ~2x hop cost documented in the latency budget.

## 7. Spin policy

```csharp
public struct SpinPolicy
{
    // ProdProfile: pure busy spin. Pause() every iteration (SMT-friendly,
    // power-throttle-friendly). Never yields, never sleeps.
    // LabProfile: spin 10_000 iters → Thread.Yield() — for shared dev boxes.
    public void Idle();
    public void NoteWork();
}
```

`Thread.SpinWait` is not used directly because its adaptive backoff escalates to
yields; we want flat `Pause`. Implementation uses
`System.Runtime.Intrinsics.X86.X86Base.Pause()` with an `ArmBase.Yield()` arm
fallback and a portable `Thread.SpinWait(1)` final fallback.

## 8. Time

- All hot timestamps are `Stopwatch.GetTimestamp()` (rdtsc-backed, invariant TSC
  verified at startup via CPUID flag; refuse to start without invariant TSC in
  strict mode).
- Wall-clock correlation: telemetry thread records `(tsc, utc)` pairs each 100 ms;
  cold-side rendering interpolates. No `DateTime.UtcNow` on hot threads (it's
  cheap, but banned for uniformity and to avoid leap-second surprises in logs).
- The timer wheel (`timer-0`): 2-level wheel, 1 µs × 65536 slots inner, fixed-size
  preallocated timer records in StaticArena; timer fire publishes a
  `TimerEvent` into the consumer's input ring — timers never invoke callbacks
  cross-thread.

## 9. Startup/shutdown sequencing

```
Init      : load config → create arenas → create rings → construct engine objects
Warmup    : touch pages → JIT-warm (Doc 08 §2) → connect sessions (no trading)
            → environment verification → Seal() rings/pools → GC.Collect(2, Forced, blocking: true, compacting: true)
            → GCSettings.LatencyMode = SustainedLowLatency → assert no-alloc probe
Trading   : enable strategies; no-alloc contract armed
Drain     : stop strategies → cancel-all confirmed → flush rings
Shutdown  : close sessions → dump telemetry → dispose arenas
```

Phase transitions are published via a single `Volatile` int read by all threads;
phase-illegal operations (e.g. `Arena.Alloc` in Trading) fail-fast (Doc 10 §4.2).

## 10. Test plan

- **Affinity tests**: each hot thread asserts `sched_getcpu()` (Linux) stays in
  its mask across 1e8 frames; failure = environment regression.
- **Jitter test**: idle-loop frame-time histogram on the lab host; p99.99 frame
  gap < 5 µs with isolation configured — this is the canary for IRQ/SMT/conf drift.
- **Heartbeat/stall injection**: pause a hot thread via debugger-attach simulation
  in lab; assert `HotThreadStalled` fires and the configured risk-off action runs.
- **Failover drill**: kill gateway thread's host process; verify exchange-side
  cancel-on-disconnect assumptions documented per venue (Doc 10 §7).
