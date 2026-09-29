using System.Diagnostics;

namespace OmlTerminal.Core.Rdp;

/// <summary>
/// Hands an RDP session off to Windows' own mstsc.exe rather than embedding a from-scratch client, so NLA/CredSSP
/// (which Windows enforces by default) is handled exactly as Microsoft's client handles it.
///
/// Credentials are never written to disk: if a username is given, they're staged with `cmdkey /generic:TERMSRV/host`
/// immediately before launching mstsc (which reads Windows-vault credentials for that target automatically) and
/// removed again once mstsc has had time to start. This avoids the classic "password in a plaintext .rdp file" mistake.
/// </summary>
public static class RdpLauncher
{
    /// <summary>mstsc's /v: target - host, or host:port when the port isn't the RDP default.</summary>
    public static string BuildTarget(string host, int port) => port is 3389 or 0 ? host : $"{host}:{port}";

    public static string CredentialTarget(string host) => $"TERMSRV/{host}";

    public static Process LaunchMstsc(string target)
    {
        var psi = new ProcessStartInfo("mstsc.exe") { UseShellExecute = true };
        psi.ArgumentList.Add("/v:" + target);
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start mstsc.exe.");
    }

    /// <summary>Runs cmdkey.exe with the given arguments and waits for it to exit. Returns its exit code.</summary>
    public static int RunCmdKey(params string[] args)
    {
        var psi = new ProcessStartInfo("cmdkey.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start cmdkey.exe.");
        p.WaitForExit(5000);
        return p.ExitCode;
    }

    /// <summary>Stages credentials, launches mstsc, and removes the staged credential shortly after. Best-effort cleanup: if the process is killed before the delay elapses, the credential is left in the Windows vault until the user removes it manually.</summary>
    public static async Task<Process> ConnectAsync(string host, int port, string username, string password, TimeSpan? credentialLifetime = null)
    {
        bool staged = false;
        if (!string.IsNullOrEmpty(username))
        {
            RunCmdKey($"/generic:{CredentialTarget(host)}", $"/user:{username}", $"/pass:{password}");
            staged = true;
        }

        var proc = LaunchMstsc(BuildTarget(host, port));

        if (staged)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(credentialLifetime ?? TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                RunCmdKey($"/delete:{CredentialTarget(host)}");
            });
        }
        return proc;
    }
}
