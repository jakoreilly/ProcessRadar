# Process Radar — Plan

Live 2D graph of running processes and their relationships — parent/child spawn lines,
DLL/handle links, injection and anomaly patterns highlighted in real time.

## Stack
C# / .NET 10, WPF, Microsoft.Diagnostics.Tracing.TraceEvent (ETW), SkiaSharp

## Risk
Full ETW process/handle tracing needs an admin/elevated session; some signals (e.g.
cross-process memory writes) are noisy and will need real tuning to avoid false-positive
spam.

## Build phases

1. **Process enumeration** — ETW (Event Tracing for Windows) process-start/stop events for
   live updates, backed by a periodic WMI/Toolhelp32 snapshot for full state.
2. **Relationship graph** — Tree model from parent PID; enrich with loaded-module list and
   open handles (via NtQuerySystemInformation) for cross-links beyond pure parent/child.
3. **Layout & render** — Radial/tree layout (root = System/services), new nodes animate in
   with a spawn "pop", short-lived processes shown as a quick flash-and-fade.
4. **Anomaly rules** — Flag: unsigned binary spawning from an unusual parent (e.g. Office →
   cmd.exe), process hollowing signatures, DLL loaded from a non-standard path, orphaned
   process re-parented to a suspicious owner.
5. **Injection highlighting** — Cross-reference OpenProcess/WriteProcessMemory-style ETW
   events between processes; draw a distinct "injection" edge (red, animated dash) between
   the two nodes involved.
6. **Polish** — Click node for detail panel (path, hash, signer, command line), search/
   filter by name, "freeze frame" to inspect a burst of activity after the fact.

## Use cases
- Live malware behavior demo — run a sample in a VM, watch it spawn children and inject in
  real time on the graph instead of reading a log after the fact.
- "What just installed that spawned 40 processes" triage — visually catch install scripts,
  updaters, or bloatware chains that are hard to piece together from Task Manager.
- Process injection detection — dedicated injection-edge highlight turns a log-diving task
  into something you'd literally see flash on screen.
- Dev/debugging aid — watch your own multi-process app spawn/exit and see unexpected
  zombie or orphaned children immediately.
- Portfolio/demo piece — pairs with Network Radar for a two-panel "live SOC dashboard" demo.

## Status
- [x] Phase 0: repo scaffolded (`ProcessRadar.sln`, WPF project, TraceEvent/SkiaSharp refs)
- [x] Phase 1: process enumeration (`Capture/ProcessEnumerationService.cs`: WMI snapshot + ETW live trace, builds clean)
- [x] Phase 2: relationship graph (`Graph/ProcessGraph.cs` - locked parent/child map fed by
      snapshot + ETW start/stop; `EnrichModules` uses managed `Process.Modules` rather than
      NtQuerySystemInformation, since it needs no P/Invoke - handle-based cross-links from the
      original plan are deferred, not implemented)
- [x] Phase 3: layout & render (`Graph/RadialTreeLayout.cs` recomputed each frame from current
      parent/child state; `Render/ProcessGraphRenderer.cs` + `MainWindow`'s `SKElement` - new
      nodes scale in over 300ms from `FirstSeen`, exited nodes flash-and-fade over 1s from
      `LastSeen`)
- [x] Phase 4: anomaly rules (`Analysis/AnomalyDetector.cs` - unsigned-interpreter-from-
      trusted-app, non-standard module path, and orphaned-process heuristics; `EnrichModules`
      (dead code since Phase 2) is now called per spawn off the UI thread to feed the module-path
      check. Flagged nodes marked via `ProcessGraph.MarkAnomaly` and glow red for 2.5s in
      `ProcessGraphRenderer`, plus a running log in the new "Anomalies" panel. Process-hollowing
      signatures and full re-parent tracking need deeper introspection than the current
      enumeration/graph model captures and are deferred, same pattern as the Phase 2 handle-based
      cross-links)
- [x] Phase 5: injection highlighting (no literal OpenProcess/WriteProcessMemory ETW events are
      available outside the restricted Threat-Intelligence provider - see
      `Capture/ProcessEnumerationService.cs`'s doc comment for the reasoning - so this uses
      `VirtualMemAlloc` events cross-referenced against a live thread->owning-process map built
      from `ThreadStart`/`ThreadStop`: an allocation whose acting thread belongs to a different
      process than the allocation's target process is flagged as `InjectionSignal` and drawn as a
      red animated-dash edge in `Render/ProcessGraphRenderer.cs`, tracked in
      `Graph/ProcessGraph.cs` (`MarkInjection`/`InjectionEdges`, 30s retention) and logged to the
      Anomalies panel via a new `AnomalyKind.CrossProcessMemoryWrite`)
- [x] Phase 6: polish (click a node for a detail panel - path, loaded-module count, first/last
      seen, anomaly state - via `MainWindow`'s new click-to-inspect overlay, same hit-test-against-
      last-painted-positions approach as Network Radar's Phase 6; a name filter textbox live-
      filters both the process list and the graph itself (`ProcessGraph.NameFilter`); "Freeze
      frame" pauses the render/housekeeping timers so a burst of activity can be inspected without
      new spawns/exits animating it away, independent of the live ETW/graph state underneath so
      unfreezing shows what happened while paused)
