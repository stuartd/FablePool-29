# 09 — Failure-Mode Analysis

Status: Design — Milestone 2
Depends on: 01–08

---

## 1. Method and Scope

This document is an FMEA (failure mode & effects analysis) over the allocation-free
architecture. For each mechanism introduced in docs 01–08 we ask: *how does it fail, how
do we detect it, what is the blast radius, and what is the designed response?* The
analysis deliberately includes failures *caused by* the zero-allocation design itself —
removing the GC removes a class of problems and introduces others (exhaustion, lifetime
bugs, fixed capacities). Pretending otherwise is how these systems hurt people.

Severity scale:
- **S1** — incorrect trading behavior possible (wrong order, corrupted message). Must be structurally prevented, not just detected.
- **S2** — trading halts or degrades on the affected instrument/session.
- **S3** — latency degradation; trading continues.
- **S4** — observability/operational annoyance only.

Designed responses use the policy ladder from doc 08 §3.1: `FailFast` (CI/soak),
`Quarantine` (UAT), `AlarmOn` + defined runtime behavior (production).

---

## 2. Object Pool Failures (doc 02)

| ID | Failure mode | Cause | Effect | Sev | Detection | Designed response |
|---|---|---|---|---|---|---|
| P-1 | Pool exhaustion | Sizing error; leak (checkout without return); burst beyond design load | `Rent()` cannot supply an object | S2 | Pool watermark metrics; `Rent` returns failure code | **Never allocate, never block.** `TryRent` returns `false`; caller executes its declared backpressure action (§2.1). Watermark alarms at 70/85/95% so ops sees exhaustion coming, not arriving. |
| P-2 | Leak (slow exhaustion) | Missing return on an error path | Pool drains over hours | S2 (delayed) | Per-pool outstanding-count trend; soak gate memory-ceiling check; debug builds tag each checkout with owner + sequence and report stale checkouts (age > N s) | Leak telemetry names the renting call site (debug/soak builds store rent-site id in the object header, doc 02 §6). Production: alarm at trend threshold; intraday mitigation is the *reclaim list* — objects stale beyond the protocol-level maximum lifetime are forcibly reclaimed **only** for object types whose protocol guarantees a bounded lifetime (e.g., session-scoped buffers on session death, §6 R-2). |
| P-3 | Double return | Same object returned twice; two owners believe they hold it | Two threads mutate one object → corrupt order state | **S1** | Generation counter in object header: return stamps a new generation; a second return with a stale generation is detected atomically | Structural prevention first: ownership analyzer (doc 01 §7) makes return sites unique on each path. Runtime check is the backstop: stale-generation return → `FailFast` in CI; in production the duplicate return is **rejected** (object stays out of the free list — leaks safely, P-2 machinery reports it) and an S1 alarm fires. Leak-on-suspicion is the correct production bias: a leaked object degrades; a doubly-owned object corrupts. |
| P-4 | Use-after-return | Holder keeps a reference after returning | Reader sees an object now owned by another flow | **S1** | Generation counter again: every `Handle<T>` (doc 02 §5) embeds the generation at rent time; each dereference validates it (one compare, ~1 ns) | Stale dereference returns failure/`default`, alarms S1. CI: `FailFast` + the soak gate runs pools in *poison mode* (returned objects are memset to a sentinel pattern) so any read of returned memory produces loud, deterministic garbage instead of plausible stale values. |
| P-5 | Pool sized for wrong distribution | Capacity planning vs. real venue behavior diverges (e.g., options chains widen) | Chronic high watermark | S3→S2 | Daily watermark report; capacity headroom KPI (peak < 60% of capacity) | Resize is an **Init-phase** action: config change + restart before next session. No runtime growth, by contract (doc 08 §2.4). |

### 2.1 Backpressure actions on `TryRent == false`

Every hot-path rent site must declare its action (enforced by the in-house analyzer —
the result of `TryRent` may not be ignored):

- **Market-data enrichment objects** → drop enrichment, forward the raw update (degraded
  but correct), increment `degraded_ticks` counter.
- **Order objects** → *reject the trading decision* (do not send), alarm S2. An
  unsendable order is safe; a half-built one is not.
- **Outbound message buffers** → backpressure to the strategy layer via the ring buffer's
  full signal (§3).

---

## 3. Ring Buffer Failures (doc 05)

| ID | Failure mode | Cause | Effect | Sev | Detection | Designed response |
|---|---|---|---|---|---|---|
| R-1 | Buffer full (producer side) | Consumer slower than producer; consumer stalled | Producer cannot claim a slot | S2/S3 | `TryClaim` failure; occupancy gauge; consumer-lag (sequence delta) metric | Policy is **declared per buffer at Init** (doc 05 §7): market-data fan-in uses *conflation* (overwrite-oldest for last-value-semantics feeds like top-of-book; price levels keyed by slot) — never blocks, never drops the *latest* state. Order-flow buffers use *reject upward*: `TryClaim` fails → strategy decision is rejected (same rationale as P-1). **No buffer may silently drop order-flow messages.** |
| R-2 | Slow/stalled consumer | Consumer thread descheduled (see T-2), livelocked, or crashed | Lag grows; eventually R-1 | S2 | Heartbeat sequence per consumer, watchdog on control thread (doc 07 §6): consumer must advance or beat every 10 ms | Watchdog escalation ladder: 10 ms → telemetry mark; 100 ms → S2 alarm + automatic *cancel-on-disconnect-style* safety action for buffers feeding the order gateway (pull quotes / flatten per strategy policy); consumer dead (no beat 1 s) → controlled session halt. The safety action is pre-armed at Init so executing it allocates nothing. |
| R-3 | Torn read of a slot | Reader observes a slot mid-write | Corrupt message consumed | **S1** | Structural prevention: sequence-stamped slots (doc 05 §4) — writer publishes the sequence with release semantics *after* the payload; reader validates sequence before and (for the overwriting/conflating variant) after copying the payload | Mismatch → retry the slot read (bounded spins), then treat as R-2 lag. Single-writer-per-buffer rule (doc 07 §3) plus power-of-two sizing makes the window provably small. Unit tests include a torn-read stressor with an artificially tiny buffer under contention. |
| R-4 | Sequence wrap / ABA | 64-bit sequence wraps | Stale slot accepted | S1 (theoretical) | None needed at runtime | 2^63 sequence values at 10^8 msgs/s ≈ 2,900 years. Documented as analyzed-and-accepted; 64-bit sequences are mandatory (32-bit forbidden by review checklist). |
| R-5 | False-sharing regression | Refactor moves cursors onto one cache line; padding struct silently shrinks | Throughput collapse, latency spikes — *looks like* R-2 | S3 | Nightly soak latency-regression gate; `hw` counters (HITM) in the perf harness | `CursorPad` types are `[StructLayout(LayoutKind.Explicit, Size = 128)]` with a static-assert test that `Unsafe.SizeOf` matches; perf harness traps regressions before production. |

---

## 4. Arena / Unmanaged Memory Failures (doc 06)

| ID | Failure mode | Cause | Effect | Sev | Detection | Designed response |
|---|---|---|---|---|---|---|
| A-1 | Arena exhaustion | Frame produced more transient data than the high-water design (e.g., full-depth snapshot storm) | `Arena.Allocate` cannot satisfy | S2 | Per-arena high-water metric each `Reset()`; `TryAllocate` failure | **Never grow in SteadyState.** `TryAllocate` fails → the owning frame aborts cleanly: market-data frame falls back to raw-forwarding (as P-1); order frame rejects the decision. Arenas are sized to ≥ 4× observed peak from replay profiling, asserted in the nightly soak. |
| A-2 | Use-after-reset | Pointer/span into the arena retained across the frame boundary | Reads another frame's data | **S1** | Structural: arena spans are `ref struct`-scoped to the frame (doc 06 §5, doc 04 rules 3/6) so escape is a compile error in safe code. Debug/soak: `Reset` poisons the region (memset `0xCD`) and, on supporting platforms, the *guard build* re-protects pages (`PROT_NONE`/`PAGE_NOACCESS`) between frames so any stale pointer faults immediately | CI guard build runs the full replay suite; any access violation pinpoints the escape. Production relies on the structural rule plus poison-pattern detection in soak. |
| A-3 | Buffer overrun within arena | Bad length arithmetic in serializer | Adjacent allocation corrupted | **S1** | All arena access flows through `Span<byte>` (bounds-checked); raw-pointer paths are confined to the two audited interop call sites (doc 06 §8) and guarded by canary words in debug/soak builds (checked at `Reset`) | Canary mismatch → `FailFast` (CI/soak), S1 alarm + session halt for the affected component (production). The design rule — spans everywhere except audited interop — makes this class nearly extinct. |
| A-4 | Commit-time page faults | Pages reserved but not touched during Init; first touch in SteadyState faults (soft, or worse, hard under memory pressure) | Multi-µs stalls early in session | S3 | First-session latency histogram vs. baseline | Init pre-touches every page (write one byte per 4 KB, doc 06 §4), locks memory (`mlock`/`VirtualLock`) where privileges allow, and the host checklist disables swap on trading hosts. Startup validation fails Boot if locking is configured-required but unavailable. |
| A-5 | Native leak outside arenas | Interop library allocates natively per call | Working set climbs; OOM after days | S2 | Soak memory-ceiling gate; RSS trend alarm in production | Third-party native usage is wrapped at Init-time-allocated bindings only (doc 06 §8); soak gate catches violations introduced by library upgrades. |

---

## 5. Threading & Core-Pinning Failures (doc 07)

| ID | Failure mode | Cause | Effect | Sev | Detection | Designed response |
|---|---|---|---|---|---|---|
| T-1 | Hot thread death | Unhandled exception escapes the loop | Its buffers stall (→ R-2 cascade) | S2 | Top-level catch in the thread proc + watchdog heartbeat | The loop's outer catch is an amnesty scope (doc 08 §2.5): record diagnostics to the pre-allocated crash ring, execute the pre-armed safety action (pull quotes / cancel session orders via the *independent* safety channel, which is pinned to its own core precisely so it survives), then either restart the loop (idempotent components, e.g., feed decoder re-syncs via snapshot) or halt the session (stateful components, e.g., order state machine — restart risk is S1). Restartability is declared per component at registration. |
| T-2 | Hot thread descheduled | Pinning failed silently; interrupt/SMI storm; another process on the core; CPU frequency transitions | Latency spikes, R-2 lag | S3→S2 | Watchdog heartbeat gap histogram; jitter sentinel thread (doc 07 §8) measuring loop-interval outliers; startup validation that affinity *took* (read it back) and that the core is in the isolated set (`isolcpus`/`cset` on Linux) | Startup fails Boot if the pinned-core checklist (isolation, SMT sibling parked, governor=performance, IRQ affinity moved away) is violated and config says `required`. Runtime gaps → S3 alarm with core id; ops runbook covers BIOS/SMI and noisy-neighbor diagnosis. |
| T-3 | Priority inversion / lock convoy | Hidden lock shared between hot and cold thread (often inside a library) | Hot thread blocks behind cold thread | S2 | Soak runs with lock-contention EventPipe provider (`Microsoft-Windows-DotNETRuntime`, Contention keyword) — **zero contention events on hot threads** is a soak pass criterion | Design rule: hot threads own their data, communicate only via the ring buffers; locks are banned in hot-path projects (Gate A bans `lock`, `Monitor`, `Mutex`, `SemaphoreSlim`). Library-internal locks are why the soak contention gate exists. |
| T-4 | Cross-thread publication bug | Missing release/acquire on a hand-rolled exchange | Stale reads; heisenbugs | **S1** | Code rule: *all* cross-thread communication uses the ring buffers or the small audited set of `Volatile`/`Interlocked` primitives in `ZeroAlloc.Concurrency`; hand-rolled fences elsewhere are banned by review checklist + analyzer | Confinement of the hard memory-model code to one audited, stress-tested module is the response; the module ships with multi-hour interleaving stress tests on weak-ordering hardware (ARM) in nightly CI. |
| T-5 | Watchdog/control thread death | Bug in control plane | Loss of enforcement & escalation, not of trading | S3 | Hot threads cheaply verify watchdog liveness (read its heartbeat word every ~1 s of iterations) | Trading continues (control plane is not in the data path); S2 alarm via the independent telemetry channel; session ends at the next natural boundary. |

---

## 6. Runtime / GC / Platform Failures (doc 08 context)

| ID | Failure mode | Cause | Effect | Sev | Detection | Designed response |
|---|---|---|---|---|---|---|
| G-1 | Contract violation: hot thread allocates | New code path missed by gates; library upgrade allocates internally; rare branch first executed live (escaped warmup) | Possible GC during session | S3 (one-off) → S2 (per-message) | `AllocationGuard` (exact, doc 08 §3.1) + EventPipe sidecar attribution | Production: alarm with type name, trade on (one small allocation is microseconds of risk; halting is worse). The *per-message* case trips the GC sentinel quickly → escalate to S2, ops decides per runbook. The escaped-warmup case feeds back into the warmup route list — every production violation must produce either a code fix or a new warmup route, tracked to closure. |
| G-2 | GC triggered by cold threads | Cold-path allocation exceeds budget; gen-2 forced by LOH allocation | Stop-the-world suspends hot threads too | S3 | GC sentinel (`CollectionCount` deltas + `GCStart/GCEnd` events with pause durations) | Cold-thread budgets (doc 08 §3.2) enforced in soak; optional `TryStartNoGCRegion` sized for the session; cold threads use pooling too — "cold" means *latency-tolerant*, not *sloppy*. LOH allocations are banned process-wide after Init. |
| G-3 | JIT activity in SteadyState | Rare branch hits a never-jitted method; tiering recompile; generic instantiation over a new type | First-call latency spike (can be ms) | S3 | EventPipe `MethodJitting*` events after the steady-state marker = soak failure; production sidecar counts them | Warmup completeness criteria (doc 08 §3.3) + replay profiles that exercise error/recovery branches; teams may additionally use R2R composite images so cold branches at least have native code. Every production jit-event is triaged like G-1's escaped-warmup case. |
| G-4 | Finalizer surprise | A type with a finalizer was pooled/retained; finalizer thread resurrects work or frees native memory we still use | Native use-after-free | **S1** | Init-time reflection audit: every pooled/arena-referenced type is verified finalizer-free (and `IDisposable` only with the explicit lifecycle from doc 01 §5); audit failure fails Boot | Structural prevention; no runtime case remains by construction. |
| G-5 | Runtime/OS upgrade changes behavior | .NET servicing release alters tiering thresholds, GC defaults, event schemas; kernel update changes scheduler/IRQ behavior | Gates or guards misbehave; latency profile shifts | S3 | The **same gates** run as a post-upgrade qualification suite on the bench host before any upgrade reaches trading hosts | Upgrade runbook: pin runtime via `global.json`/self-contained publish; qualify on bench (Gates B, C, soak, jitter sentinel baseline diff); schema-version tolerance in event parsing (doc 08 §4.3 name-based payload lookup). |
| G-6 | Clock anomalies | NTP step, TSC instability across cores | Timestamps misordered; watchdog false alarms | S2 (data) | Cross-check monotonic vs. wall clock on control thread; startup validates invariant-TSC | All latency math uses the monotonic clock (`Stopwatch`/`rdtsc` policy per doc 07 §9); wall time is annotation-only. Steps > tolerance → data-quality flag on outbound timestamps + S2 alarm. |

---

## 7. Operational / Process Failures

| ID | Failure mode | Cause | Effect | Sev | Detection | Designed response |
|---|---|---|---|---|---|---|
| O-1 | Config drift between bench and prod | Capacity/pinning configs diverge | Gates pass on hardware that doesn't match production | S2 | Config hash is part of the host's startup banner and the soak report; mismatch alarms | Single source of truth: `topology.toml` + `capacity.toml` deployed atomically with the binary; prod host refuses to start (`required` mode) on unknown topology. |
| O-2 | Replay profiles go stale | Market microstructure changes (tick-size regime, new venue behavior) | Warmup/soak no longer represent live load | S3→S2 | Quarterly profile-refresh task; "live vs. replay" drift report compares message-rate and burst histograms weekly | Profiles are rebuilt from recent capture on schedule; the drift report failing two weeks running blocks the next release until refreshed. |
| O-3 | Amnesty creep | Developers wrap inconvenient allocations in amnesty scopes | Contract hollows out | S3 (cultural, compounding) | Amnesty reasons are a closed enum requiring a PR to extend; per-reason counters trend-reported nightly | New enum members need platform-team sign-off; nightly trend regressions assigned as bugs. The enum-gated design makes the social failure visible in code review and in metrics. |
| O-4 | Gate flakiness erodes trust | Noisy bench host → intermittent Gate C latency failures → developers learn to re-run until green | Real regressions slip through | S2 (indirect) | Gate-pass-rate dashboard; flake-rate SLO (< 2%) | Bench host gets the full isolation checklist (T-2); *allocation* criteria are deterministic (zero is zero) and never waived; only latency criteria use statistical bands, and those run thrice-sampled with median comparison. |

---

## 8. Cross-Cutting Conclusions

1. **The S1 rows share one shape: lifetime/aliasing bugs.** The design answers them in
   layers — (a) compile-time: ownership analyzer, `ref struct` scoping; (b) cheap
   always-on runtime checks: generation counters, sequence-stamped slots, bounds-checked
   spans; (c) loud debug/soak modes: poisoning, page guards, canaries. No S1 row relies
   on detection alone.
2. **Exhaustion replaces collection.** GC pressure failures become capacity failures
   (P-1, R-1, A-1). Capacity failures are *better*: they are observable in advance
   (watermarks), bounded (fixed memory), and have declared per-site backpressure
   semantics. The invariant across all of them: **never allocate, never block, never
   silently drop order flow.**
3. **The watchdog/control plane is the only component allowed to be "smart" at runtime**;
   hot threads only ever execute pre-armed actions. Every escalation in this document
   resolves to an action that was fully constructed during Init.
4. **Every production anomaly must close a loop**: violation → fix or new warmup
   route/replay scenario → gate that would have caught it. The FMEA table is a living
   document; each incident review checks whether its row existed and whether the designed
   response held.
