# Process Radar

A live 2D graph of the processes running on a Windows machine: parent/child
spawn lines, loaded-module enrichment, and anomaly heuristics highlighted as
they happen. Built on ETW (Event Tracing for Windows) and WMI, rendered with
SkiaSharp in a WPF app.

## What this is — and what it isn't

Process Radar is a **read-only observability tool**. It watches; it never
injects, suspends, terminates, or modifies another process. Elevation is
required only to open an ETW kernel session — nothing else in the app needs it.

It is **not** an antivirus, an EDR, or a detection product, and should not be
relied on as one:

- The "injection" signal is a single narrow heuristic — a `VirtualMemAlloc`
  whose acting thread belongs to a different process than the allocation's
  target. That's the classic `VirtualAllocEx` precursor to injection, but the
  write itself is never observed. Real OpenProcess/WriteProcessMemory events
  live in the restricted `Microsoft-Windows-Threat-Intelligence` provider,
  which is reserved for protected-process antimalware and is not subscribable
  from a normal process even when elevated. See the doc comment in
  [`Capture/ProcessEnumerationService.cs`](ProcessRadar/Capture/ProcessEnumerationService.cs)
  for the full reasoning.
- The anomaly rules are heuristics over the parent/child map, executable path,
  and loaded modules. They will produce false positives on a normal desktop and
  will miss anything that doesn't fit their shape.

Treat it as a way to *see* process behaviour that would otherwise mean reading
a log — a triage, teaching, and debugging aid, not a security control.

## Requirements

- Windows
- .NET 10 SDK (`dotnet --version` should report 10.x)
- Administrator rights **only** for the live ETW trace. The one-shot
  **Snapshot** view works fine unelevated.

## Quick run

Double-click **`run.cmd`**. It requests elevation (UAC prompt), builds, and
launches the app.

## Manual run

```
dotnet run --project ProcessRadar\ProcessRadar.csproj -c Release
```

Run from an **elevated** terminal for live tracing and injection detection —
without admin, only **Snapshot** works.

## Using the app

1. **Snapshot** — one-shot WMI process list, builds the graph as it stands right
   now. Works without admin.
2. **Start live trace (admin)** — subscribes to ETW process/thread events for
   continuous updates: new spawns animate in with a "pop", exits flash-and-fade.
   Requires elevation.
3. The graph is a radial tree from the root (System/services). Red
   animated-dash edges mark a flagged cross-process memory allocation, retained
   for 30s.
4. **Anomalies panel** (bottom right) logs unsigned-interpreter-from-trusted-app,
   non-standard module path, orphaned-process, and cross-process-memory-write
   detections; the matching node glows red for 2.5s.
5. **Filter box** — live-filters both the process list and the graph by name.
6. **Click any node** for the inspector overlay: path, loaded-module count,
   first/last seen, anomaly state, SHA-256, signer, command line.
7. **Freeze frame** pauses rendering so you can inspect a burst of activity
   without new spawns/exits animating it away. Unfreezing catches you up to
   whatever happened underneath while paused.
8. **Zoom / pan** — mouse wheel to zoom, drag to pan, double-click to reset.
9. **Export logs** — saves the full process table (independent of the filter
   box) and the whole-session anomaly history to one CSV file, for attaching
   to a bug report or reviewing offline. Runs off the UI thread so exporting a
   long-running trace doesn't stall the window.

## Diagnostics and privacy

The **Debug** toggle turns on an on-canvas performance HUD and writes a
per-run log to:

```
%LOCALAPPDATA%\ProcessRadar\logs\processradar-<timestamp>.log
```

**It is off by default, deliberately.** That log records executable paths and
loaded-module paths from the machine it ran on, which routinely embed the
operator's username and directory layout. Nothing is ever transmitted anywhere —
the app makes no network connections — but review a log before attaching it to
a bug report or sharing it.

The click-to-inspect panel also surfaces command lines (via WMI), which can
contain credentials passed as arguments by other software. That data stays
on screen and is never written to the log or an export.

**Export logs** (in the toolbar) writes the same kind of data — executable
paths, process names, anomaly descriptions — to a CSV file you choose.
Nothing is transmitted anywhere; review the file before attaching it to a
bug report or sharing it, same as the debug log. Text fields that a
spreadsheet would otherwise treat as a formula are escaped on the way out,
since process names and paths are chosen by whatever is being observed.

## Troubleshooting

- **Live trace button does nothing** — the process isn't elevated. Re-run via
  `run.cmd` or from an admin terminal.
- **No injection signals ever fire** — expected in normal use. See the scope
  note above; this is a narrow heuristic, not a detection engine.
- **Anomalies panel floods on a busy machine** — the heuristics are intentionally
  loose. Use the filter box to narrow the graph.

## Building

```
dotnet build ProcessRadar.slnx -c Release
```

There is no automated test suite; CI builds the solution on `windows-latest`.

## License

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
