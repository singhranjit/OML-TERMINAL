using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Voice;

/// <summary>
/// Turns one spoken (or typed) instruction into the CLI lines to send:
///   "go to configure terminal and then go to interface gigabit ethernet zero slash zero, add IP address
///    ten dot zero dot zero dot one slash twenty four and bring it up"
///   → configure terminal / interface GigabitEthernet0/0 / ip address 10.0.0.1 255.255.255.0 / no shutdown
/// The text is normalized (<see cref="SpokenNormalizer"/>), split into phrases on "and", "then", commas and sentence
/// breaks, stripped of conversational filler, and each phrase is translated by the vendor grammar. Pure string
/// logic - no audio involved - so typing, speech and macros all behave identically.
/// </summary>
public static partial class VoiceCommandParser
{
    [GeneratedRegex(@"\s+\b(?:and\s+then|and|then|after\s+that)\b\s+|\s*;\s*|\s*,(?!\s*\d)\s*|(?<!\d),\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Separators();

    [GeneratedRegex(@"^(?:(?:please|now|ok(?:ay)?|so|then|also|and|hey|alright|right|first|finally|lastly|can\s+you|could\s+you|would\s+you|will\s+you|i\s+want\s+to|i'd\s+like\s+to|i\s+would\s+like\s+to|i\s+need\s+to|let's|lets|let\s+us|go\s+ahead\s+and|just)\b\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingFiller();

    [GeneratedRegex(@"\s+(?:please|for\s+me|now)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingFiller();

    public static IReadOnlyList<string> Expand(string spokenText, IVendorGrammar grammar)
    {
        if (string.IsNullOrWhiteSpace(spokenText)) return [];
        var normalized = SpokenNormalizer.Normalize(spokenText);
        return Separators().Split(normalized)
            .Select(p => TrailingFiller().Replace(LeadingFiller().Replace(p.Trim(), ""), "").Trim())
            .Where(p => p.Length > 0)
            .Select(grammar.Translate)
            .Where(p => p.Length > 0)
            .ToList();
    }

    /// <summary>
    /// True only when every line is a complete, real-looking command for this platform - the bar hands-free mode must
    /// clear before sending anything without the user reviewing it.
    /// </summary>
    public static bool SafeToAutoSend(IReadOnlyList<string> lines, IVendorGrammar grammar) =>
        lines.Count > 0 && lines.All(l => grammar.LooksLikeCli(l) && !LooksIncomplete(l) && CliValidator.Problem(l) is null);

    /// <summary>The utterance ended mid-command ("ip address" ... pause ... "10.1.1.1 255.255.255.0"): the last line
    /// is only missing its value, so hands-free should wait for the next utterance instead of sending or rejecting.</summary>
    public static bool EndsMidCommand(IReadOnlyList<string> lines) =>
        lines.Count > 0 && (LooksIncomplete(lines[^1]) || CliValidator.Problem(lines[^1]) is "missing a value" or "no address given" or "no subnet mask given")
        && lines.Take(lines.Count - 1).All(l => CliValidator.Problem(l) is null);

    /// <summary>Phrases that still look like instructions rather than CLI after translation ("add ip address" with no
    /// address): shown as warnings so nothing half-understood is typed into a device unnoticed.</summary>
    public static bool LooksIncomplete(string line) => IncompletePhrase().IsMatch(line.Trim());

    [GeneratedRegex(@"^(?:add|set|assign|give|put|create|make|change|go\s+to|enter)\b(?!.*\d)", RegexOptions.IgnoreCase)]
    private static partial Regex IncompletePhrase();
}
