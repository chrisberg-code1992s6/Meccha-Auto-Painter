# Runtime Research Tools

This directory contains explicit, authenticated tools for investigating a
running game. They are excluded from normal release builds and must never add a
second paint transport.

Build the diagnostic runner without replacing the normal package output:

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/research/build-replication-runner.ps1
```

The runner requires `MECCHA_RESEARCH_ARTIFACTS=1` and an exact game PID:

```text
AutoPainter.exe --research-replication --pid <pid> \
  --role host|joining-client --out <local-artifact-directory> [--paint] \
  [--hold-seconds N] [--pressure-sample-ms N] [--texture-snapshot] [--texture-sample-ms N] \
  [--texture-target resolved|inventory-all|eventwatch-direct-receiver --texture-discovery-seconds N] \
  [--stroke-limit N] [--queue-target N] [--replay-stroke-index N] \
  [--front-mode paint|fill|skip] [--side-mode paint|fill|skip] [--back-mode paint|fill|skip] \
  [--cancel-after-ms N | --shutdown-after-ms N] [--fill-color '#RRGGBB'] \
  [--paint-color '#RRGGBB'] [--metallic 0..1] [--roughness 0..1] [--emissive 0..1]
```

The runner injects its own authenticated bridge instance, stages event-watch
sidecars before injection, and records JSON artifacts under the selected output
directory. Do not run two research runners against one game at the same time.

## Direct-paint contract

Research paint uses the same production route as the app: one reflected
`PaintAtUVWithBrush` call per planned stroke. The game owns recording,
rendering, and multiplayer replication. Preview and Unpreview are the only
operations allowed to use texture import/export.

There is no packed RPC, compact payload, no-resend route, receiver-queue route,
radius override, or batch/pacing option. A missing direct-paint schema is a
failed run, not a reason to select another transport.

The bridge records native queue telemetry with every paint progress sample:

- submitted strokes
- completed strokes (`submitted - component/recorded queue depth` when readable)
- component/recorded/global queue observations
- configured native queue target, peak, and wait count
- one-millisecond scheduler cadence and CPU-budget yields

Treat a terminal reply as valid only after the native queue is idle. Compare
painter and joining-client results independently; a fast paint submission does
not prove remote visible completion.

`--queue-target N` (1 through 16) is a research-only high-water-mark override
for the game's recorded-paint queue. It does not change normal application
settings. Use it to compare one fixed stroke plan at `1`, `4`, `8`, and `16`;
the game-reported per-tick capacity remains an upper bound on the effective
value.

## Texture and material probes

`--texture-snapshot` performs low-frequency before/after exports of the
resolved component. It records all material channels and an Albedo changed-pixel
mask. On a joining client, `--texture-target eventwatch-direct-receiver` pins
the exact `PaintAtUVWithBrush` receiver seen by event-watch. The run fails if
that receiver is absent or changes before the after probe.

`--texture-target inventory-all` is a passive receiver diagnostic. It exports
checksums for every eligible `RuntimePaintableComponent` before and after a
peer paint, so it can prove a remote texture mutation even when replication
does not enter `ProcessEvent`. It is deliberately not used for sender timing
or normal paint verification, and it retains raw JSON inventories rather than
rendering a single-target PNG delta.

With `--texture-snapshot`, `--texture-sample-ms N` records a compact texture
time series while a non-paint observer is holding. Each sample preserves the
initial per-component baseline, so changed-pixel counts show the game's actual
replicated render-target drain rather than only the final checksum. It requires
a positive `--hold-seconds`; use it sparingly because channel exports run on
the game thread.

For material investigations, use distinguishable values such as
`M=.21/R=.83/E=.47`. Manual Paint values apply only when Auto Detect is off.
Auto Detect selects the game-reported global dominant pattern for Paint;
Fill always uses its explicit manual material.

Every successful research paint requests a post-plan UV sidecar and renders a
bounded `uv-replay-atlas.png`. Its two columns are Fill and Paint;
each footprint uses the direct planner radius. The PNG is diagnostic data, not a
screenshot or proof of in-game rendering.

## Cancellation and shutdown

`--cancel-after-ms` and `--shutdown-after-ms` are separate lifecycle tests.
They require `--paint`, are mutually exclusive, and record both the control
reply and paint terminal reply. A cancellation is only successful if native
admission latches it or the active direct-paint job reaches a cancelled terminal
state. Shutdown must report `active_paint_quiescent=true` and stop event-watch.

## Policy

- Keep generated artifacts outside the repository or under ignored paths.
- Do not commit game archives, mappings, event-watch output, dumps, or replay
  payloads.
- Use numeric queue and texture evidence before changing game-facing code.
- Do not lower the one-millisecond scheduler floor or mutate game queue memory
  to improve a benchmark.
