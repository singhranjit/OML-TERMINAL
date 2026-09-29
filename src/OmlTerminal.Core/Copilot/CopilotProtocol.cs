namespace OmlTerminal.Core.Copilot;

public enum CopilotReplyKind
{
    /// <summary>The model wants to run one command and see its output before continuing.</summary>
    Run,
    /// <summary>The model considers the task finished; Text is the final answer for the operator.</summary>
    Done,
    /// <summary>The model didn't follow the RUN:/DONE: protocol - shown as-is so nothing is silently dropped.</summary>
    Say,
}

public sealed record CopilotReply(CopilotReplyKind Kind, string Text);

/// <summary>The text protocol between the copilot loop and the model: one command per turn, no function-calling
/// required, so it works with whatever a local model happens to support.</summary>
public static class CopilotProtocol
{
    public static CopilotReply Parse(string reply)
    {
        var trimmed = (reply ?? "").Trim();
        var lines = trimmed.Replace("\r\n", "\n").Split('\n');

        var runLine = lines.FirstOrDefault(l => l.TrimStart().StartsWith("RUN:", StringComparison.OrdinalIgnoreCase));
        if (runLine is not null)
        {
            var cmd = runLine[(runLine.IndexOf(':') + 1)..].Trim();
            if (cmd.Length > 0) return new CopilotReply(CopilotReplyKind.Run, cmd);
        }

        var doneIdx = trimmed.IndexOf("DONE:", StringComparison.OrdinalIgnoreCase);
        if (doneIdx >= 0) return new CopilotReply(CopilotReplyKind.Done, trimmed[(doneIdx + "DONE:".Length)..].Trim());

        return new CopilotReply(CopilotReplyKind.Say, trimmed);
    }

    public static string SystemPrompt(string deviceName, string host, string protocolLabel) => $"""
        You are a network operations copilot with direct command access to one device over {protocolLabel}: "{deviceName}" ({host}).
        You can run exactly one command per turn and will be shown its output before choosing the next one.

        Respond with EXACTLY one line in one of these two forms, and nothing else:
        RUN: <the single command to run next>
        DONE: <your final answer to the operator>

        Rules:
        - One command per turn. Never chain multiple commands or add extra commentary outside the RUN:/DONE: line.
        - Prefer read-only / diagnostic commands first. Only run something that changes configuration or state if the
          task genuinely requires it, and expect the operator to be asked to approve it first.
        - If a command fails or the output isn't what you expected, explain your next step via another RUN: - don't
          repeat the same command unchanged.
        - Use DONE: as soon as you can answer the operator's request. Don't keep running commands "just in case".
        """;
}
