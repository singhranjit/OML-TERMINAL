namespace OmlTerminal.Core.Persistence;

public sealed class SessionValidationException(IReadOnlyList<string> errors)
    : Exception("Invalid session: " + string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
