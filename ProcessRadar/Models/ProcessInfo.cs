namespace ProcessRadar.Models;

public sealed record ProcessInfo(
    int Pid,
    int ParentPid,
    string Name,
    string? Path,
    DateTime? StartTime);
