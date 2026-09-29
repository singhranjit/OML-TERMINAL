using System.Text.Json;
using OmlTerminal.Core.Persistence;
using OmlTerminal.Core.Terminal;

namespace OmlTerminal.Core.Workflows;

/// <summary>A user-reviewed sequence of CLI checks, stored without credentials or device addresses.</summary>
public sealed class TroubleshootingWorkflow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Commands { get; set; } = new();
    public int DelayMs { get; set; } = 500;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Workflow name is required.");
        if (Commands.Count == 0 || Commands.Count > 100) errors.Add("A workflow must contain between 1 and 100 commands.");
        if (Commands.Any(string.IsNullOrWhiteSpace)) errors.Add("Workflow commands cannot be empty.");
        if (Commands.Any(c => c.Length > 2000)) errors.Add("A workflow command cannot exceed 2000 characters.");
        if (DelayMs is < 0 or > 60000) errors.Add("Delay must be between 0 and 60000 ms.");
        return errors;
    }
}

public static class WorkflowRunner
{
    /// <summary>Runs only after the caller has shown the command preview and received explicit confirmation.</summary>
    public static async Task RunAsync(TerminalSession session, TroubleshootingWorkflow workflow, CancellationToken ct = default)
    {
        var errors = workflow.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        foreach (var command in workflow.Commands)
        {
            ct.ThrowIfCancellationRequested();
            if (!session.IsConnected) throw new InvalidOperationException("The target session disconnected before the workflow finished.");
            session.Send(System.Text.Encoding.UTF8.GetBytes(command.TrimEnd() + "\r"));
            if (workflow.DelayMs > 0) await Task.Delay(workflow.DelayMs, ct).ConfigureAwait(false);
        }
    }
}

public sealed class JsonWorkflowStore(string? path = null)
{
    public string Path { get; } = path ?? System.IO.Path.Combine(AppPaths.DataDirectory, "workflows.json");

    public List<TroubleshootingWorkflow> Load()
    {
        if (!File.Exists(Path)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<TroubleshootingWorkflow>>(File.ReadAllText(Path), JsonFile.Options)?
                .Where(w => w is not null && w.Validate().Count == 0).ToList() ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public void Save(IEnumerable<TroubleshootingWorkflow> workflows)
    {
        var list = workflows.ToList();
        foreach (var workflow in list)
        {
            var errors = workflow.Validate();
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        }
        JsonFile.WriteAtomic(Path, list);
    }
}
