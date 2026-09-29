using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Persistence;

/// <summary>One session recovered from another client's saved config, plus any note about what couldn't be brought over.</summary>
public sealed record ImportResult(SessionProfile Profile, string? Note = null);

public sealed class ImportException(string message) : Exception(message);
