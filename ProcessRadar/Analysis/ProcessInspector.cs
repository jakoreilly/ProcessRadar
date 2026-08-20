using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ProcessRadar.Analysis;

public sealed record ProcessInspectionDetail(string? Sha256, string? Signer, string? CommandLine);

/// <summary>
/// Phase 6: off-UI-thread lookups for the click-to-inspect panel - hashing a multi-MB executable,
/// reading its Authenticode signature, and a WMI round trip for the command line are all real I/O,
/// so <see cref="Inspect"/> is meant to be called via <c>Task.Run</c>, matching the existing
/// <see cref="Graph.ProcessGraph.EnrichModules"/> pattern of doing per-process I/O off the click/UI
/// thread and posting results back once they land.
/// </summary>
public static class ProcessInspector
{
    public static ProcessInspectionDetail Inspect(int pid, string? path)
    {
        string? hash = null;
        string? signer = null;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            hash = TryHash(path);
            signer = TrySigner(path);
        }

        return new ProcessInspectionDetail(hash, signer, TryCommandLine(pid));
    }

    private static string? TryHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // See AnomalyDetector's identical suppression: X509CertificateLoader can't extract an
    // Authenticode signature embedded in a signed PE, only load a standalone cert file.
#pragma warning disable SYSLIB0057
    private static string? TrySigner(string path)
    {
        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(path);
            return cert.Subject;
        }
        catch
        {
            // Unsigned, unreadable, or gone by the time we checked - all read as "no signer".
            return null;
        }
    }
#pragma warning restore SYSLIB0057

    private static string? TryCommandLine(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject mo in searcher.Get())
                return mo["CommandLine"] as string;
        }
        catch (ManagementException)
        {
            // Process exited between click and lookup, or WMI unavailable - routine, not exceptional.
        }
        return null;
    }
}
