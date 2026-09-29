namespace OmlTerminal.Core.CliGuide;

/// <summary>The CLI mode a command runs in - shown as a badge and used to group commands.</summary>
public enum CliMode { Exec, Privileged, Config, ConfigInterface, ConfigRouter, Operational, Diagnostic }

/// <summary>One reference entry: a command, what it does, its syntax and a worked example.</summary>
public sealed record CliCommand(
    string Command,
    string Description,
    CliMode Mode,
    string Category,
    string Syntax = "",
    string Example = "")
{
    /// <summary>Everything searchable about this entry, lower-cased once for fast contains-matching.</summary>
    public string Haystack { get; } = $"{Command} {Description} {Category} {Syntax} {Example}".ToLowerInvariant();

    public static string ModeLabel(CliMode m) => m switch
    {
        CliMode.Exec => "EXEC",
        CliMode.Privileged => "ENABLE",
        CliMode.Config => "CONFIG",
        CliMode.ConfigInterface => "IF-CONFIG",
        CliMode.ConfigRouter => "ROUTER",
        CliMode.Operational => "OPER",
        CliMode.Diagnostic => "DIAG",
        _ => m.ToString().ToUpperInvariant(),
    };
}

/// <summary>A vendor's full command reference.</summary>
public sealed record CliVendor(string Id, string Name, string Prompt, IReadOnlyList<CliCommand> Commands)
{
    public IReadOnlyList<string> Categories =>
        Commands.Select(c => c.Category).Distinct().OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
}
