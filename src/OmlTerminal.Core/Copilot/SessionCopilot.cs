using System.Runtime.CompilerServices;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Copilot;

public sealed record CopilotEvent(string Kind, string Text);

/// <summary>Drives a RUN:/DONE: conversation (see <see cref="CopilotProtocol"/>) between a local LLM and one
/// device's own side-channel shell (<see cref="DeviceShell"/>). Every command the model proposes is classified by
/// <see cref="CommandRisk"/>; anything not recognized as read-only stops and asks <paramref name="confirmRisky"/>
/// (in <see cref="RunAsync"/>) before it's ever sent to the device.
/// Note: C# iterators can't yield from inside a catch block, so every step below catches into a local error
/// variable first and yields the "error" event afterward, outside the try/catch.</summary>
public sealed class SessionCopilot(LlmClient llm, LlmSettings settings)
{
    private const int MaxSteps = 12;
    private const int MaxOutputChars = 4000;

    public async IAsyncEnumerable<CopilotEvent> RunAsync(SessionProfile device, string userRequest,
        Func<string, Task<bool>> confirmRisky, [EnumeratorCancellation] CancellationToken ct = default)
    {
        DeviceShell? shell = null;
        string? connectError = null;
        try { shell = await DeviceShell.OpenAsync(device, ct); }
        catch (Exception ex) { connectError = $"Could not connect to {device.Name}: {ex.Message}"; }
        if (connectError is not null) { yield return new CopilotEvent("error", connectError); yield break; }

        using (shell!)
        {
            var messages = new List<ChatMessage>
            {
                new("system", CopilotProtocol.SystemPrompt(device.Name, device.Host, device.ProtocolLabel)),
                new("user", userRequest),
            };

            for (var step = 0; step < MaxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();

                string? reply = null;
                string? llmError = null;
                try { reply = await llm.CompleteAsync(settings, messages, ct); }
                catch (Exception ex) { llmError = $"Model request failed: {ex.Message}"; }
                if (llmError is not null) { yield return new CopilotEvent("error", llmError); yield break; }

                messages.Add(new ChatMessage("assistant", reply!));
                var parsed = CopilotProtocol.Parse(reply!);

                if (parsed.Kind == CopilotReplyKind.Done) { yield return new CopilotEvent("done", parsed.Text); yield break; }
                if (parsed.Kind == CopilotReplyKind.Say) { yield return new CopilotEvent("assistant", parsed.Text); continue; }

                var command = parsed.Text;
                yield return new CopilotEvent("command", command);

                if (CommandRisk.IsRisky(command))
                {
                    yield return new CopilotEvent("confirm-needed", command);

                    bool approved = false;
                    string? confirmError = null;
                    try { approved = await confirmRisky(command); }
                    catch (Exception ex) { confirmError = $"Approval check failed: {ex.Message}"; }
                    if (confirmError is not null) { yield return new CopilotEvent("error", confirmError); yield break; }

                    if (!approved)
                    {
                        yield return new CopilotEvent("declined", command);
                        messages.Add(new ChatMessage("user", "That command was not approved - do not run it. Try a different approach, or use DONE: to stop."));
                        continue;
                    }
                }

                string output;
                try { output = await shell.RunAsync(command, TimeSpan.FromSeconds(6), ct); }
                catch (Exception ex) { output = $"(error running command: {ex.Message})"; }

                yield return new CopilotEvent("output", output);
                messages.Add(new ChatMessage("user", $"Output:\n{Truncate(output, MaxOutputChars)}"));
            }

            yield return new CopilotEvent("error", "Stopped after reaching the step limit without a final answer.");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n... (truncated)";
}
