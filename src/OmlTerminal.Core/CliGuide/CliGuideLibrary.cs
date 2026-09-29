namespace OmlTerminal.Core.CliGuide;

/// <summary>
/// The built-in CLI reference: every supported vendor's commands, with case-insensitive multi-term search. Search
/// terms are ANDed ("show bgp" matches entries containing both words anywhere), so it works as a quick "how do I..."
/// lookup as well as an exact command finder.
/// </summary>
public static class CliGuideLibrary
{
    public static IReadOnlyList<CliVendor> Vendors { get; } = CliGuideData.BuildAll();

    public static CliVendor? Find(string id) =>
        Vendors.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Commands in a vendor matching every whitespace-separated term in <paramref name="query"/> (blank = all),
    /// optionally limited to one category. Ordered so a command whose name starts with the query comes first.</summary>
    public static IReadOnlyList<CliCommand> Search(CliVendor vendor, string query, string? category = null)
    {
        var terms = (query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var q = (query ?? "").Trim().ToLowerInvariant();

        return vendor.Commands
            .Where(c => category is null || string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(c => terms.All(t => c.Haystack.Contains(t)))
            .OrderByDescending(c => q.Length > 0 && c.Command.ToLowerInvariant().StartsWith(q))
            .ThenBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Command, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static int TotalCommands => Vendors.Sum(v => v.Commands.Count);
}
