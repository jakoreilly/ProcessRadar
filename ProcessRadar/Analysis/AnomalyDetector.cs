using System.Security.Cryptography.X509Certificates;
using ProcessRadar.Graph;
using ProcessRadar.Models;

namespace ProcessRadar.Analysis;

public enum AnomalyKind { UnsignedFromTrustedApp, NonStandardModulePath, OrphanProcess, CrossProcessMemoryWrite }

public sealed record AnomalyEvent(AnomalyKind Kind, int Pid, string Description, DateTime Timestamp);

/// <summary>
/// Phase 4: heuristics layer. Two of the plan's four signals ("process hollowing signatures" and
/// full re-parent tracking) need deeper introspection than the current enumeration/graph model
/// captures and are deferred, matching how handle-based cross-links were deferred in Phase 2 -
/// these three are what the existing data (parent/child map, path, loaded modules) supports:
/// an unsigned interpreter spawning from a commonly-targeted app, a module loaded from outside
/// Windows/Program Files, and a process whose parent isn't tracked at all.
/// </summary>
public sealed class AnomalyDetector
{
    // Interpreters/shell hosts commonly abused as the next stage after an initial-access
    // document/link is opened - "Office spawns cmd.exe" is the textbook living-off-the-land case.
    private static readonly HashSet<string> ScriptHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe",
        "mshta.exe", "rundll32.exe", "regsvr32.exe",
    };

    // Apps that routinely handle untrusted content (documents, email, web, PDFs) - a shell
    // spawning from one of these is far more suspicious than from explorer.exe or a dev terminal.
    private static readonly HashSet<string> CommonlyTargetedApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe", "msaccess.exe",
        "chrome.exe", "msedge.exe", "firefox.exe",
        "acrord32.exe", "acrobat.exe", "foxitreader.exe",
    };

    private static readonly string?[] StandardModuleRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    ];

    /// <summary>Evaluated when a process is first seen via a live transition (not the bulk
    /// snapshot load - that would flag most of a normal system's already-orphaned processes,
    /// since a WMI snapshot routinely catches processes whose parent already exited, and drown
    /// the log on the very first click).</summary>
    public IReadOnlyList<AnomalyEvent> EvaluateNewProcess(ProcessInfo info, ProcessGraph graph)
    {
        var events = new List<AnomalyEvent>();
        var now = DateTime.Now;

        var parent = graph.TryGetNode(info.ParentPid);
        if (parent is not null
            && ScriptHostNames.Contains(info.Name)
            && CommonlyTargetedApps.Contains(parent.Name)
            && !string.IsNullOrEmpty(info.Path)
            && !IsAuthenticodeSigned(info.Path))
        {
            events.Add(new AnomalyEvent(AnomalyKind.UnsignedFromTrustedApp, info.Pid,
                $"unsigned {info.Name} (pid {info.Pid}) spawned from {parent.Name} (pid {info.ParentPid})", now));
        }

        if (info.ParentPid != 0 && info.ParentPid != info.Pid && parent is null)
        {
            events.Add(new AnomalyEvent(AnomalyKind.OrphanProcess, info.Pid,
                $"orphaned process: {info.Name} (pid {info.Pid}), parent pid {info.ParentPid} not tracked", now));
        }

        return events;
    }

    /// <summary>Evaluated once module enrichment completes for a process - that's a syscall per
    /// module, so it runs off the UI thread and lands here later than the spawn event itself.</summary>
    public IReadOnlyList<AnomalyEvent> EvaluateModules(ProcessNode node)
    {
        var events = new List<AnomalyEvent>();
        foreach (var module in node.Modules)
        {
            if (string.IsNullOrEmpty(module.Path))
                continue;
            if (StandardModuleRoots.Any(root => !string.IsNullOrEmpty(root) &&
                    module.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                continue;

            events.Add(new AnomalyEvent(AnomalyKind.NonStandardModulePath, node.Pid,
                $"{node.Name} (pid {node.Pid}) loaded {module.Name} from non-standard path: {module.Path}",
                DateTime.Now));
        }
        return events;
    }

    // X509CertificateLoader (the SYSLIB0057-suggested replacement) loads a standalone certificate
    // file, not the Authenticode signature embedded in a signed PE - it can't do this job, and
    // there's no supported non-P/Invoke replacement for CreateFromSignedFile's PE-extraction
    // behavior yet, so the obsolete call stays with the warning suppressed here rather than
    // pulled in via WinVerifyTrust P/Invoke.
#pragma warning disable SYSLIB0057
    private static bool IsAuthenticodeSigned(string path)
    {
        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(path);
            return cert is not null;
        }
        catch
        {
            // No signature, unreadable file, or file gone by the time we checked - all read as
            // "can't confirm signed", not exceptional.
            return false;
        }
    }
#pragma warning restore SYSLIB0057
}
