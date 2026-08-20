namespace ProcessRadar.Models;

public readonly record struct LoadedModule(string Name, string Path);

public sealed class ProcessNode
{
    public required int Pid { get; init; }
    public int ParentPid { get; set; }
    public required string Name { get; set; }
    public string? Path { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public bool Alive { get; set; } = true;
    public IReadOnlyList<LoadedModule> Modules { get; set; } = [];
    public DateTime? LastAnomalyAt { get; set; }
}
