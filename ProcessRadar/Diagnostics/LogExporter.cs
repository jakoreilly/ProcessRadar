using System.Globalization;
using System.IO;
using System.Text;
using ProcessRadar.Analysis;
using ProcessRadar.Models;

namespace ProcessRadar.Diagnostics;

/// <summary>
/// Writes the current process table and the anomaly history to a single CSV file on request (the
/// "Export logs" button) - a one-shot user action, unlike the always-on <see cref="DebugLog"/>.
/// The two tables share one file (blank line + fresh header between them) so the export stays a
/// single artifact to attach to a bug report, the same way <see cref="DebugLog"/> already treats
/// one file as one session's worth of evidence. Same privacy shape as the debug log too: this
/// records executable paths, which routinely embed the operator's username and directory layout.
/// </summary>
public static class LogExporter
{
    public static void Export(string path, IReadOnlyList<ProcessNode> processes, IReadOnlyList<AnomalyEvent> anomalies)
    {
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);

        writer.WriteLine("# Processes");
        writer.WriteLine("Pid,ParentPid,Name,Path,StartTime,FirstSeen,LastSeen,Alive,LoadedModules,LastAnomalyAt");
        foreach (var n in processes.OrderBy(n => n.Pid))
        {
            writer.WriteLine(string.Join(",",
                Field(n.Pid),
                Field(n.ParentPid),
                Field(n.Name),
                Field(n.Path ?? ""),
                Field(n.StartTime?.ToString("O") ?? ""),
                Field(n.FirstSeen.ToString("O")),
                Field(n.LastSeen.ToString("O")),
                Field(n.Alive),
                Field(n.Modules.Count),
                Field(n.LastAnomalyAt?.ToString("O") ?? "")));
        }

        writer.WriteLine();
        writer.WriteLine("# Anomalies");
        writer.WriteLine("Timestamp,Kind,Pid,Description");
        foreach (var a in anomalies)
        {
            writer.WriteLine(string.Join(",",
                Field(a.Timestamp.ToString("O")),
                Field(a.Kind.ToString()),
                Field(a.Pid),
                Field(a.Description)));
        }
    }

    private static string Field(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Field(bool value) => value ? "true" : "false";

    /// <summary>Characters that make Excel / LibreOffice treat a cell as a formula rather than
    /// text. Every string in this export is attacker-influenced - a process can be named
    /// <c>=cmd|'/c calc'!A1</c> - and the whole point of the file is that someone opens it in a
    /// spreadsheet to triage an incident, so the export must not be the thing that runs code on
    /// the analyst's machine. Quoting alone does not help: a leading quote is stripped on parse.</summary>
    private static readonly char[] FormulaLeaders = ['=', '+', '-', '@', '\t', '\r'];

    private static string Field(string value)
    {
        // Prefixing with a tab (rather than dropping or replacing the character) keeps the original
        // text intact and readable for anything parsing the CSV as data.
        if (value.Length > 0 && Array.IndexOf(FormulaLeaders, value[0]) >= 0)
            value = "\t" + value;

        if (value.IndexOfAny([',', '"', '\n', '\r', '\t']) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
