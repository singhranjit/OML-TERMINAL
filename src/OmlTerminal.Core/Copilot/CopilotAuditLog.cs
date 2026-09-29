using OmlTerminal.Core.Backup;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Copilot;

/// <summary>A plain-text, append-only record of every request, command and approval decision for a copilot run -
/// this thing can send commands to real infrastructure on its own, so every run leaves a trail on disk regardless
/// of whether anyone is watching the chat panel.</summary>
public static class CopilotAuditLog
{
    public static string Start(SessionProfile device)
    {
        var dir = Path.Combine(AppPaths.DataDirectory, "copilot-logs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{ConfigBackup.SafeFileName(device.Name)}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
    }

    public static void Append(string path, string line)
    {
        try { File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {line}\n"); }
        catch { /* logging must never take the copilot down */ }
    }
}
