using System.Diagnostics;

namespace OmlTerminal.Core.Scripting;

/// <summary>Whatever the picked session (if any) contributes to a run - handed to the script as OML_SESSION_*
/// environment variables, plus optional text piped to its stdin.</summary>
public sealed record ScriptContext(string? SessionName, string? Host, string? Username, string? Protocol, string? Folder, string? Stdin);

/// <summary>Launches a <see cref="ScriptDefinition"/> as a plain external process - stdout/stderr streamed line by
/// line, killed on cancellation. No sandboxing beyond what the OS process boundary already gives you: a script can
/// do anything the signed-in user could do from a terminal, and nothing the app's own process can do beyond that
/// (it never sees the credential vault or other sessions).</summary>
public sealed class ScriptRunner
{
    public event Action<string>? LineReceived;

    public async Task<int> RunAsync(ScriptDefinition script, ScriptContext context, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = script.Interpreter,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = context.Stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in script.InterpreterArgs) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(script.Path);

        void SetEnv(string key, string? value) { if (!string.IsNullOrEmpty(value)) psi.Environment[key] = value; }
        SetEnv("OML_SESSION_NAME", context.SessionName);
        SetEnv("OML_SESSION_HOST", context.Host);
        SetEnv("OML_SESSION_USER", context.Username);
        SetEnv("OML_SESSION_PROTOCOL", context.Protocol);
        SetEnv("OML_SESSION_FOLDER", context.Folder);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) LineReceived?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) LineReceived?.Invoke("[stderr] " + e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var killOnCancel = ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });

        if (context.Stdin is not null)
        {
            // WriteAsync itself has no cancellation overload, so a script that never reads stdin (filling the
            // pipe buffer) would otherwise block here regardless of ct until the process happens to exit or die.
            // WaitAsync(ct) makes cancellation return promptly instead - the kill-on-cancel registration above
            // still tears the process down, which unblocks (and faults, harmlessly) the abandoned write.
            try { await process.StandardInput.WriteAsync(context.Stdin).WaitAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* pipe broke because the process already exited or was killed - fall through to WaitForExitAsync */ }
            if (!ct.IsCancellationRequested) { try { process.StandardInput.Close(); } catch { } }
        }

        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}
