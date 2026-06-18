# Section 10 — Consolidated Recommendations & Implications for Subsequent Milestones

> Part of the FablePool "Solve Garbage Collection in C# for HFT" research survey.
> This section synthesizes Sections 02–09 into an actionable, tiered recommendation set
> and states the concrete engineering implications that the build milestones which follow
> this survey should inherit. Reference tags ([R1]–[R24]) resolve in
> `09-annotated-references.md`.

---

## 10.1 The core finding, restated in one paragraph

The .NET GC cannot be configured into hard-real-time behavior; it can only be configured
into *infrequent, bounded-in-practice* behavior. Every latency mode, heap knob, and
runtime improvement surveyed in Sections 04–06 reduces the *frequency* or *average cost*
of pauses, but none eliminates the tail: a Gen2/LOH compacting collection or an
ill-timed thread suspension can still appear at the worst possible moment. The only
strategy that survives contact with a 99.99th-percentile latency budget in the
single-digit-microsecond range is to make the hot path **allocation-free during the
trading session**, so that the GC is given nothing to do, and to confine all unavoidable
allocation to startup, end-of-day, or explicitly tolerated maintenance windows. GC
tuning is then the *second* line of defense — it bounds the damage when the
allocation-free discipline is imperfect — not the first.

## 10.2 Tiered recommendation set

The tiers below are ordered by return on engineering investment, derived from the
mitigation comparison in Section 08. Each tier assumes the previous tiers are in place.

### Tier 0 — Measurement before mitigation (mandatory, ~days)

1. Stand up the pause-measurement harness from **Appendix A** in the actual deployment
   environment (same OS, same core isolation, same NIC stack). Numbers measured on a
   developer laptop are not admissible evidence.
2. Record a baseline: GC pause histogram, allocation rate (bytes/sec per thread),
   Gen0/Gen1/Gen2/LOH collection counts over a simulated full trading session.
3. Define the latency budget as a percentile contract (e.g., "order-trigger to
   wire ≤ 8 µs at p99.9, ≤ 50 µs at p99.99, no single event > 250 µs during
   continuous trading"). Every later decision is judged against this contract.

### Tier 1 — Configuration floor (mandatory, ~hours)

Apply regardless of architecture; see Appendix B for exact syntax and caveats:

- **Server GC**, concurrent/background enabled, on .NET 8+ (regions + DATAS-aware
  settings; pin DATAS *off* for latency determinism — see §10.4 item 3).
- **Heap affinitization** to the isolated trading cores; GC heap count matched to the
  number of managed worker threads, not the machine core count.
- `GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency` during the session.
- **Tiered compilation jitter controls**: ReadyToRun images plus
  `DOTNET_TieredPGO=0`/`DOTNET_TC_QuickJitForLoops=0` or full warmup routine, so JIT
  promotion does not masquerade as GC jitter in measurements (Appendix A §A.6).
- LOH compaction left *off* during the session; scheduled compaction (if needed) only
  in maintenance windows via `GCSettings.LargeObjectHeapCompactionMode`.

### Tier 2 — Hot-path allocation elimination (the core work, ~weeks–months)

In the priority order established by the allocation catalog (Section 07):

1. **Buffer and object pooling** for all market-data and order-message objects;
   pre-sized at startup from measured session maxima ×2 headroom.
2. **Ban LINQ, closures, and iterator methods** on hot paths; enforce with analyzers
   and CI allocation-budget tests (Appendix C).
3. **Boxing elimination**: generic constraints instead of interface dispatch on
   structs; `ISpanFormattable`-based formatting instead of `string.Format`/
   interpolation with value types.
4. **String discipline**: `ReadOnlySpan<char>`/UTF-8 byte parsing of feed data without
   materializing strings; pooled `char[]`/`byte[]` for outbound formatting; symbol
   interning table built at startup.
5. **Async removal from the hot path**: replace `async/await` with single-threaded
   event loops or busy-spin reactors per core; where async is unavoidable
   (control plane), use `ValueTask`, `IValueTaskSource`, and pooled state machines
   (`PoolingAsyncValueTaskMethodBuilder`) — control plane only.
6. **Collection pre-sizing**: every `List<T>`/`Dictionary<TKey,TValue>` on a session
   lifetime is constructed at startup with measured capacity; growth during the
   session is treated as a bug and asserted against in soak tests.

### Tier 3 — Structural containment (~weeks)

- **Process separation**: hot path in a minimal process with a small, stable heap;
  control plane, risk aggregation, logging, and UI in separate processes communicating
  over shared memory / lock-free SPSC ring buffers. A GC in the control-plane process
  then cannot suspend trading threads at all.
- **`GC.TryStartNoGCRegion` windows** around known critical sequences (auction open,
  scheduled economic releases), with budget sized from Tier-0 measurements and a
  tested fallback for `InvalidOperationException`/budget exhaustion.
- **Off-heap state** (memory-mapped files, `NativeMemory`-allocated arenas accessed
  through `Span<T>`) for large, long-lived data sets (order books, symbol universes)
  so they never enter the GC's marking workload.

### Tier 4 — Residual-risk acceptance or escalation

After Tiers 0–3, the measured residual is typically rare Gen1 pauses in the tens of
microseconds and very rare Gen2 background-GC suspension pairs (Section 06). Options:

- **Accept**, with monitoring alarms on GC counts during session hours (most shops).
- **Escalate** to native interop for the final hop (C/C++/Rust NIC handler feeding a
  managed strategy layer) — only justified if the percentile contract is still
  violated *and* the violation is attributable to GC rather than OS/NIC jitter, which
  Tier-0 instrumentation must be able to demonstrate.

## 10.3 Decision flow (one page)

```
                         ┌──────────────────────────────┐
                         │ Latency contract defined?    │
                         │ (percentiles + hard ceiling) │
                         └──────────┬───────────────────┘
                                    │ no → STOP: define it (Tier 0)
                                    ▼
                         ┌──────────────────────────────┐
                         │ Baseline measured in prod-   │
                         │ like environment? (App. A)   │
                         └──────────┬───────────────────┘
                                    │ no → build harness first
                                    ▼
              ┌─────────────────────────────────────────────┐
              │ Does baseline already meet contract with    │
              │ Tier-1 configuration alone?                 │
              └──────┬──────────────────────────┬───────────┘
                     │ yes                      │ no
                     ▼                          ▼
            Ship + monitor GC counts   ┌────────────────────────────┐
            during session hours       │ Largest measured contributor│
                                       └──────┬─────────────────────┘
                     ┌────────────────────────┼──────────────────────┐
                     ▼                        ▼                      ▼
            Allocation rate high      Pauses rare but        Pauses caused by
            (Gen0 > ~1/min in         long (Gen2/LOH)        non-GC sources
            session)                                          (OS, JIT, NIC)
                     │                        │                      │
                     ▼                        ▼                      ▼
            Tier 2: eliminate         Tier 3: process         Fix environment
            hot-path allocation       separation, NoGC        (core isolation,
            (Section 07 catalog,      regions, off-heap       R2R/warmup, IRQ
            App. C enforcement)       state                   steering) — App. A
                     │                        │
                     └──────────┬─────────────┘
                                ▼
                   Re-measure against contract
                                │
                  meets? ───────┼──────── still fails after
                     │          │          Tiers 0–3, GC-attributed
                     ▼          ▼
                  Ship      Tier 4: native interop for
                            final hop, managed strategy layer
```

## 10.4 Hard-won caveats the later milestones must inherit

1. **Zero-allocation is a property to be enforced, not assumed.** Every later
   milestone that delivers hot-path code must ship with the CI allocation-budget
   tests of Appendix C wired in from the first commit. Allocation regressions are the
   single most common way low-latency .NET systems decay in production [R14], [R19].
2. **`NoGCRegion` is a scalpel, not a mode.** Its budget interacts with heap hard
   limits and Server GC heap count in non-obvious ways (Section 04 §4.6); the exit
   path on budget exhaustion is a stop-the-world collection at the worst time if not
   handled. Later milestones must treat entry failure and mid-region exhaustion as
   first-class tested code paths.
3. **DATAS (.NET 8+) optimizes for memory footprint, not tail latency.** Dynamic heap
   count adaptation can resize heaps mid-session. For HFT, pin it off
   (`DOTNET_GCDynamicAdaptationMode=0`) and size heaps explicitly (Appendix B §B.4).
4. **Regions (.NET 7+) changed pause distributions, not pause existence.**
   Decommit/commit behavior and free-region recycling differ from segments
   (Section 05); any pause numbers gathered on .NET 6 or earlier must be re-measured.
5. **Measurement tooling itself allocates.** The EventListener-based monitor in
   Appendix A allocates per event; it is a *development/soak* tool. Production
   monitoring should read `GC.GetGCMemoryInfo`/counters at coarse intervals from a
   non-critical thread.
6. **The OS contributes jitter of the same magnitude as a tuned GC.** Until core
   isolation, IRQ steering, and power-state pinning are done, GC-attribution of tail
   events is guesswork. Appendix A §A.5 gives the environment checklist.

## 10.5 What the next milestones should build (derived scope)

| Future deliverable | Grounded in this survey | Acceptance criterion |
|---|---|---|
| Allocation-free messaging core (pooled market-data & order objects, SPSC rings) | §07.2–07.4, §08 matrices M1/M2 | 0 bytes allocated per message in steady state, proven by App. C test harness |
| GC configuration profile + boot-time validator | §04, App. B | Process refuses to start trading if any Tier-1 setting is absent |
| Pause/jitter telemetry agent | §06, App. A | < 1 µs overhead on hot threads; full pause histogram per session |
| NoGC-window scheduler for known events | §04.6, §10.4(2) | Tested exhaustion fallback; budget derived from soak measurements |
| CI allocation-budget + analyzer gate | §07, App. C | Build fails on any new hot-path allocation |
| Process-separation reference architecture | §08 matrix M3 | Control-plane full GC causes 0 ns suspension of trading threads (measured) |

---

*End of Section 10. Continue to Appendix A for the measurement methodology, Appendix B
for the configuration reference, Appendix C for allocation tooling and CI enforcement,
and Appendix D for the glossary.*
