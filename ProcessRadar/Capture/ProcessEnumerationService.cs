using System.Management;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using ProcessRadar.Models;

namespace ProcessRadar.Capture;

/// <summary>
/// Phase 1: process enumeration. A WMI snapshot gives full state (pid,
/// parent pid, path, start time) on demand; a kernel ETW session streams
/// ProcessStart/ProcessStop as they happen. Requires an elevated process
/// for the ETW session (kernel providers) - the snapshot path works
/// unelevated.
///
/// Phase 5: injection highlighting. The classic NT Kernel Logger provider used here has no
/// literal OpenProcess/WriteProcessMemory events - those require the restricted
/// Microsoft-Windows-Threat-Intelligence provider, which is reserved for protected-process
/// antimalware code and isn't subscribable from a normal (even admin) process. The closest
/// legitimate signal available here is <c>VirtualMemAlloc</c>: its ProcessID field is the
/// *target* address space, while its ThreadID belongs to whichever thread issued the syscall.
/// By tracking thread -> owning-process from ThreadStart events, a VirtualMemAlloc whose acting
/// thread belongs to a different process than the allocation's target process is a remote
/// ("VirtualAllocEx-style") allocation - the standard precursor to a WriteProcessMemory-based
/// injection, even though the write itself isn't directly observed.
/// </summary>
public sealed class ProcessEnumerationService : IDisposable
{
    private TraceEventSession? _session;
    private Task? _sessionTask;

    // Mutated only from the ETW session's single processing thread (session.Source.Process()
    // dispatches callbacks synchronously on that one background task) - same no-lock-needed
    // assumption the rest of this class already relies on for its event handlers.
    private readonly Dictionary<int, int> _threadOwner = new();
    private readonly Dictionary<int, string> _processNames = new();

    public event EventHandler<ProcessInfo>? ProcessStarted;
    public event EventHandler<int>? ProcessStopped;
    public event EventHandler<InjectionSignal>? InjectionDetected;

    public static IReadOnlyList<ProcessInfo> Snapshot()
    {
        var results = new List<ProcessInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CreationDate FROM Win32_Process");

        foreach (ManagementObject mo in searcher.Get())
        {
            var creationDateRaw = mo["CreationDate"] as string;
            DateTime? started = null;
            if (!string.IsNullOrEmpty(creationDateRaw))
                started = ManagementDateTimeConverter.ToDateTime(creationDateRaw);

            results.Add(new ProcessInfo(
                Convert.ToInt32(mo["ProcessId"]),
                Convert.ToInt32(mo["ParentProcessId"]),
                (string)(mo["Name"] ?? "?"),
                mo["ExecutablePath"] as string,
                started));
        }

        return results;
    }

    /// <summary>Starts the live ETW process-start/stop stream. Requires admin.</summary>
    public void StartLiveTrace()
    {
        if (TraceEventSession.IsElevated() != true)
            throw new InvalidOperationException("Live ETW process tracing requires an elevated (admin) process.");

        const string sessionName = "ProcessRadarKernelSession";
        TraceEventSession.GetActiveSessionNames()
            .Where(n => n == sessionName)
            .ToList()
            .ForEach(n => new TraceEventSession(n) { StopOnDispose = true }.Dispose());

        _session = new TraceEventSession(sessionName) { StopOnDispose = true };
        _session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.Process
            | KernelTraceEventParser.Keywords.Thread
            | KernelTraceEventParser.Keywords.VirtualAlloc);

        _session.Source.Kernel.ProcessStart += data =>
        {
            _processNames[data.ProcessID] = data.ProcessName;
            ProcessStarted?.Invoke(this, new ProcessInfo(
                data.ProcessID,
                data.ParentID,
                data.ProcessName,
                data.ImageFileName,
                data.TimeStamp));
        };

        _session.Source.Kernel.ProcessStop += data =>
        {
            _processNames.Remove(data.ProcessID);
            ProcessStopped?.Invoke(this, data.ProcessID);
        };

        _session.Source.Kernel.ThreadStart += data => _threadOwner[data.ThreadID] = data.ProcessID;
        _session.Source.Kernel.ThreadStop += data => _threadOwner.Remove(data.ThreadID);

        _session.Source.Kernel.VirtualMemAlloc += data =>
        {
            if (!_threadOwner.TryGetValue(data.ThreadID, out var actingPid))
                return;
            if (actingPid == 0 || data.ProcessID == 0 || actingPid == data.ProcessID)
                return;

            var sourceName = _processNames.TryGetValue(actingPid, out var name) ? name : $"pid {actingPid}";
            InjectionDetected?.Invoke(this, new InjectionSignal(
                actingPid, sourceName, data.ProcessID, data.ProcessName, data.TimeStamp));
        };

        _sessionTask = Task.Run(() => _session.Source.Process());
    }

    public void StopLiveTrace()
    {
        _session?.Dispose();
        _session = null;
        _sessionTask = null;
    }

    public void Dispose() => StopLiveTrace();
}
