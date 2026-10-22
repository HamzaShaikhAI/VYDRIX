# Benchmarks

Two kinds of benchmark, both reproducible with `benchmarks/CloudPlayX.Benchmarks`. Raw outputs (Markdown + JSON) are
in `benchmarks/results/`. Nothing here is estimated or hand-edited.

```bash
dotnet run -c Release --project benchmarks/CloudPlayX.Benchmarks -- real --mbps 70 --latency 80
```

```bash
dotnet run -c Release --project benchmarks/CloudPlayX.Benchmarks -- sim --scale 0.25
```

## 1. Real end-to-end (measured)

**Setup:** the real CLOUDPLAY X stack (AppHost, rclone 1.75.2, WinFsp, prediction engine) on the i3-10105F test PC.
The "cloud" is SimCloud, a local HTTP server throttled to **70 Mbps with 80 ms per-request latency** (Google Drive was
not available for automated runs). Workload: the DRM-free **CPX demo game** loading level 2 — 409 reads, 54.5 MiB,
from 4 × 96 MiB archives. Load time is measured by the demo game itself. Each cloud scenario starts with an empty
cache unless stated. Two complete runs:

| Scenario | Run 1 load | Run 2 load | Cloud bytes (run 2) | HTTP requests | Reads ≥ 50 ms | rclone peak RAM |
|---|---|---|---|---|---|---|
| **A** Physical SSD | 0.09 s | 0.10–0.12 s | — | — | 0 | — |
| **B** Standard rclone mount (rclone defaults: buffer 16M, chunk 128M), cold | 56.5 s | 65.2 s | 551 MiB | 65 | 89 | 142 MiB |
| **C** CLOUDPLAY X cold (no profile) | 21.0 s | 20.3 s | 117 MiB | 164 | 168 | 57 MiB |
| **D** CLOUDPLAY X warm: 2 profiled launches, cache cleared, Optimize & Prepare, *unseen* level | 8.2 s | 7.7 s | 39 MiB during play (+179 MiB in a 31.9 s Prepare) | 43 | 39 | 58 MiB |
| **D′** CLOUDPLAY X fully warm cache | 0.15 s | 0.27 s | ~0 | 0 | 0–1 | 58 MiB |

Phases (run 2): B startup/menu/level 3.6 / 44.7 / 15.7 s; C 1.1 / 9.7 / 8.6 s; D 0.29 / 1.13 / 6.2 s.

**Verdicts vs. baselines**

- vs. **A (SSD)**: cold and prepared cloud launches are **much worse** (20 s and 8 s vs 0.1 s). Fully cached is
  close but still slower (0.15–0.27 s vs 0.10 s — VFS overhead).
- vs. **B (standard rclone)**: C is **better** (≈3× faster, 4.7× fewer bytes). This gain comes mostly from the
  tuned VFS flags (`--buffer-size 0`, 4 MiB chunks), not from prediction — the stock 16 MiB read-ahead downloads
  ~10× the data a random-access loader needs. D is **better** again (≈8× faster than B) because Prepare moved
  ~179 MiB of learned startup data before launch.
- CPU: rclone used 1–3 s CPU per cold launch; with `-vv` telemetry over a whole session ~7 s.
- Frame rate was not measured and is not a storage metric.

## 2. Simulation matrix (model, not a measurement)

**Setup:** deterministic discrete-event simulator running the **production** queue, scheduling policy, governor,
predictors, accounting and SQLite profile store against a virtual clock. Network: shared bandwidth at 90 % efficiency
plus fixed per-request latency; SSD 500 MB/s; 0.15 ms VFS overhead per cache hit. Workload: synthetic game trace
(seed 11, scale 0.25): 1,584 reads touching 904 MiB; profile trained on 3 earlier sessions with different levels; the
test session's level differs. Times are total load time.

| Link | Latency | A SSD | B rclone default | B0 buffer 0, no engine | C CPX cold | D CPX profile, no Prepare | D+ profile + Prepare (Prepare time) | D+ with 256 MiB cache |
|---|---|---|---|---|---|---|---|---|
| 10 Mbps | 150 ms | 1.7 s | 4860 s | 959 s | 960 s | 959 s | **300 s** (891 s) | 994 s |
| 30 Mbps | 150 ms | 1.7 s | 1620 s | 397 s | 397 s | 394 s | **126 s** (341 s) | 419 s |
| 70 Mbps | 150 ms | 1.7 s | 698 s | 236 s | 237 s | 233 s | **76 s** (184 s) | 243 s |
| 70 Mbps | 40 ms | 1.7 s | 694 s | 153 s | 154 s | 155 s | **49 s** (136 s) | — |
| 100 Mbps | 150 ms | 1.7 s | 489 s | 200 s | 200 s | 196 s | **65 s** (148 s) | 203 s |
| 500 Mbps | 150 ms | 1.7 s | **110 s** | 133 s | 130 s | 123 s | **43 s** (81 s) | 129 s |

Hit rate: B0 52 %, D 55 %, D+ ≈ 85 % at every link speed. Prediction accuracy 88–94 % (D), ~72 % (D+).

**What the simulation says (and its limits)**

1. **Prediction does not create bandwidth.** Without Prepare, a learned profile is neutral (−8 % … +2 %) because the
   game's own demand already saturates the link.
2. **Prepare moves downloading before PLAY** — ~3× shorter loads, at the cost of a preparation step that itself takes
   roughly the time of the data it fetches.
3. **The cache must hold the working set**; with 256 MiB the prepared data was evicted before use.
4. **rclone's default read-ahead is very wasteful at ≤ 100 Mbps** for random-access loads (5 GiB fetched for 904 MiB
   needed) **but wins at 500 Mbps**, where latency dominates. Users on very fast links may set `--buffer-size`
   4–16 MiB in Settings → Advanced. (Adaptive tuning is future work.)
5. Model assumptions (equal bandwidth sharing, fixed latency, synthetic trace) are simplifications; the real benchmark
   above is the ground truth for this project.

## Methodology rules

No numbers are invented or adjusted. Each run writes a timestamped file in `benchmarks/results/`. Variance between
runs (e.g. B: 56.5 s vs 65.2 s) is reported rather than averaged away.
