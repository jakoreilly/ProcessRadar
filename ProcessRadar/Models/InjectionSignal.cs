namespace ProcessRadar.Models;

/// <summary>Raised by <see cref="Capture.ProcessEnumerationService"/> when a thread belonging to
/// one process allocates virtual memory whose target process differs from that thread's own -
/// the classic first half of the VirtualAllocEx + WriteProcessMemory injection pattern. See the
/// service's doc comment for why this (and not a literal WriteProcessMemory event) is the signal
/// used.</summary>
public sealed record InjectionSignal(int SourcePid, string SourceName, int TargetPid, string TargetName, DateTime Timestamp);

public sealed class InjectionEdge
{
    public required int SourcePid { get; init; }
    public required int TargetPid { get; init; }
    public DateTime LastSeen { get; set; }
}
