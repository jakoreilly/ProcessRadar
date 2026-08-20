# Process Radar — How to Use

Live 2D graph of running processes: parent/child spawn lines, module/injection
links, anomalies highlighted in real time. See `plan.md` for the full build
history and phase notes.

## Requirements

- Windows, .NET 10 SDK (`dotnet --version` should show 10.x)
- Administrator rights for the **live ETW trace** (process/thread start-stop,
  memory-allocation events, injection detection). The one-time **Snapshot**
  view works without admin.

## Quick run

Double-click **`run.cmd`** in this folder. It elevates itself (UAC prompt), builds,
and launches the app. That's the one-click path for everyday use.

## Manual run

```
dotnet run --project ProcessRadar\ProcessRadar.csproj -c Debug
```
Run from an **elevated** terminal to get live tracing and injection detection —
without admin, only **Snapshot** works.

## Using the app

1. **Snapshot** — one-shot WMI process list, builds the graph as it stands right
   now. Works without admin.
2. **Start live trace (admin)** — subscribes to ETW process/thread events for
   continuous updates: new spawns animate in with a "pop", exits flash-and-fade.
   Requires elevation; silently does nothing useful if not admin.
3. The graph is a radial tree from the root (System/services). Red animated-dash
   edges mark a flagged cross-process memory write (`InjectionSignal`,
   retained 30s) — the closest available signal to true injection detection,
   since raw OpenProcess/WriteProcessMemory ETW events aren't available outside
   the restricted Threat-Intelligence provider (see
   `Capture/ProcessEnumerationService.cs`).
4. **Anomalies panel** (bottom right, red-tinted list) logs unsigned-interpreter-
   from-trusted-app, non-standard module path, orphaned-process, and
   cross-process-memory-write detections; the matching node glows red for 2.5s.
5. **Filter box** — live-filters both the process list and the graph by name.
6. **Click any node** for the inspector overlay: path, loaded-module count,
   first/last seen, anomaly state, hash, signer, command line. Close with the
   `x` in the panel.
7. **Freeze frame** pauses rendering/housekeeping timers so you can inspect a
   burst of activity without new spawns/exits animating it away; unfreezing
   catches you up to whatever happened underneath while paused.

## Troubleshooting

- **Live trace button does nothing** — not elevated; re-run via `run.cmd` or an
  admin terminal.
- **No injection signals ever fire** — expected in normal use; this is a
  narrow heuristic (cross-process `VirtualAlloc` from a thread owned by a
  different process), not a general AV engine.
