# 13 — Benchmark & Validation Methodology

> This document defines how the architecture's latency and "no allocations
> after warmup" claims are measured, so that results are reproducible,
> statistically honest, and comparable across code changes. It complements the
> enforcement tooling (`tools/AllocationGate`, doc 08) with the *measurement*
> discipline that the migration guide's acceptance criteria reference.

## 13.1 What we measure

For each release candidate, on the reference hardware profile (doc 12 §12.5):

1. **Wire-to-wire latency**: market-data packet arrival (NIC hardware or
   kernel timestamp) → order bytes handed to the transmit path. This is the
   number that matters commercially.
2. **Stage latencies**: decode → book update → signal → risk → encode,
   timestamped at ring-buffer hand-offs (doc 05) using `Stopwatch.GetTimestamp()`.
3. **Hiccups**: scheduling/jitter events on pinned threads, independent of
   message flow (§13.5).
4. **Allocation behaviour**: AllocationGate over both short (CI) and soak
   (nightly) windows.
5. **GC activity**: collection counts and pause durations, which must be
   **zero after warmup** (`GC.CollectionCount(g)` deltas and
   `GC.GetGCMemoryInfo().PauseDurations` snapshots taken at window boundaries).

## 13.2 Timestamping rules

* Use `Stopwatch.GetTimestamp()` (a `long`; allocation-free) at instrumentation
  points; convert to nanoseconds only at report time:
  `ns = ticks * 1_000_000_000 / Stopwatch.Frequency`. Verify at startup that
  `Stopwatch.IsHighResolution` is true and frequency implies sub-µs
  resolution; abort the benchmark otherwise (HPET fallback invalidates
  results — doc 12 §12.5).
* Timestamps ride **inside the message structs** (doc 03 reserves the
  `IngressTimestamp` field) so stage latency needs no side lookups.
* Never timestamp with `DateTime.UtcNow` in the hot path; it is coarser and on
  some platforms slower.
* For wire-to-wire numbers, prefer NIC hardware timestamps (or capture-card
  timestamps on a mirrored port) over application timestamps; application
  receive timestamps systematically *understate* latency because they miss
  kernel/NIC queueing.

## 13.3 Recording without allocating: histograms

Latencies are recorded into **pre-allocated histograms**, never into growing
lists:

* Use HdrHistogram-style log-bucketed histograms. The recommended package is
  **HdrHistogram (.NET port, 2.5.x line)**; construct all histogram instances
  during warmup, sized `lowestDiscernible=1ns, highest=10s, 3 significant
  digits`. `RecordValue(long)` on a pre-built histogram is allocation-free and
  therefore legal in the hot path. If the dependency is unwanted in the engine
  process, the same structure (~23 KB of `long[]` buckets per histogram) is
  trivially implemented in-repo; either way the *recording* path must be
  verified by AllocationGate like any other hot-path code.
* One histogram **per stage per thread** (no cross-thread sharing; merge at
  report time on the control plane).
* Snapshot/reset happens on the control-plane thread via the command ring
  (doc 05), never by the hot thread taking a lock.

## 13.4 Coordinated omission — mandatory correction

Replay benchmarks that send the next message only after the previous one
completes will silently hide stalls: during a 5 ms hiccup, an open-loop
production feed would have delivered thousands of messages that all experienced
the stall, but a closed-loop test records *one* slow sample. This is
**coordinated omission** and it can understate p99.9 by orders of magnitude.

Rules:

1. Replay harnesses are **open-loop**: messages are injected on the schedule of
   the recorded feed's original timestamps (or a fixed target rate), regardless
   of whether the system under test has finished the previous message.
2. Latency is measured from the **intended** send time, not the actual send
   time, so backpressure shows up as latency rather than disappearing.
3. When closed-loop measurement is unavoidable, apply HdrHistogram's
   `CopyCorrectedForCoordinatedOmission(expectedInterval)` and report both raw
   and corrected distributions, clearly labelled.

## 13.5 Hiccup meter

Independent of message load, each pinned core runs (in benchmark builds) a
jHiccup-style probe: a loop that sleeps/spins for a fixed short interval,
timestamps before and after, and records `observed − expected` into a
histogram. This isolates *platform* jitter (SMIs, IRQs that escaped
affinity, frequency transitions, GC threads straying onto the core) from
*application* latency. A clean platform shows hiccup p99.99 in the low
microseconds; anything worse means doc 12 §12.5's checklist was not actually
applied, and message-latency results from that run are quarantined.

## 13.6 Reporting standards

* Report **p50 / p90 / p99 / p99.9 / p99.99 / max** and sample count. Never
  report a mean or standard deviation as a headline number — latency
  distributions are heavy-tailed and means are misleading.
* Plot full percentile curves (HDR "percentile distribution" output) for
  before/after comparisons; a single percentile can hide a crossed-over tail.
* Every report records: commit hash, runtime version, full doc-12 configuration
  dump, hardware profile, NIC/driver versions, message rate, and run duration.
  Results without provenance are not accepted in review.
* Minimum run lengths: 5 minutes for smoke comparisons; 1 hour for release
  qualification; 24-hour soak nightly. Tail percentiles need samples: p99.99
  requires ≥ 10⁶ measurements to have ~100 samples beyond it.

## 13.7 Run protocol

1. **Environment assert**: run the deployment validation script (doc 12); abort
   on any mismatch.
2. **Cold start** the engine; let the full warmup protocol complete; confirm
   the `WarmupComplete` marker.
3. **Attach AllocationGate** for the entire measurement window
   (`--max-ticks 0`).
4. **Quarantine first N seconds** after the marker (default 30 s) from latency
   statistics — caches, branch predictors, and frequency residency settle even
   after functional warmup.
5. **Replay** the canonical captured market-data day(s) open-loop at recorded
   rate, then at 2× and 5× rate for headroom characterisation.
6. **Collect**: histograms, hiccup histograms, AllocationGate JSON,
   GC counters (must show zero collections post-marker), working set and
   handle-count time series (flat lines expected — FMEA F-7/F-9 detection).
7. **Repeat ≥ 5 runs**; report the distribution *across* runs at each
   percentile. A regression is flagged when the new build's p99.9 exceeds the
   baseline's p99.9 by more than the cross-run spread (no single-run
   comparisons).

## 13.8 CI integration tiers

| Tier | Trigger | Duration | Gates |
|---|---|---|---|
| **PR gate** | every PR | ~2 min | AllocationGate zero-tick window on replay smoke; benchmark *compiles and runs*; no latency gating (CI hardware is too noisy for ns-level assertions). |
| **Nightly** | scheduled, dedicated bare-metal runner | 1 h replay + 1 h soak gate window | AllocationGate zero-tick over the soak window; GC-count delta == 0; p99.9 regression check vs. rolling baseline with cross-run spread tolerance. |
| **Release qualification** | per release | 24 h soak + full replay matrix | All of the above; working-set/handle flatness; hiccup-meter platform validation; signed report archived with the release. |

Latency assertions run **only** on the dedicated bare-metal runner with the
doc-12 profile applied; asserting nanoseconds on shared cloud CI produces
flaky gates and teaches the team to ignore red builds, which is worse than not
gating.

## 13.9 Honest-measurement checklist (reviewer crib sheet)

* [ ] Open-loop injection, or CO-corrected and labelled?
* [ ] Percentiles, not means? Max reported?
* [ ] Enough samples for the claimed percentile?
* [ ] Warmup quarantined; marker confirmed; gate attached and green?
* [ ] Environment validation log attached? Hiccup meter clean?
* [ ] ≥ 5 runs; cross-run spread shown?
* [ ] Configuration identical to production profile?

A benchmark result missing any item is returned to the author. The point of
this milestone's architecture is a *provable* property; the proof is only as
good as the measurement discipline behind it.
