using System.Text;
using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Voice;

/// <summary>
/// Turns what a speech recognizer writes down into CLI-shaped text before the vendor grammar sees it:
///   "Go to interface gigabit ethernet zero slash zero. Add IP address ten dot zero dot zero dot one slash twenty four."
///   → "Go to interface GigabitEthernet0/0, Add IP address 10.0.0.1/24"
/// Sentence breaks become phrase separators, number words become digits, "dot"/"slash" become symbols, spoken
/// interface names become Cisco-style names, and the many ways of saying "configure terminal" collapse to one.
/// Typed input passes through unchanged apart from those rewrites, which never apply to already-compact CLI text.
/// </summary>
public static partial class SpokenNormalizer
{
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Trim();
        s = SpokenVlan().Replace(s, "vlan");               // "v lan" / "vee lan" → vlan
        s = SentenceBreak().Replace(s, ", ");            // "…0/0. Add…" → "…0/0, Add…" (IPs never have a space after a dot)
        s = TrailingPunctuation().Replace(s, "");
        // Detach sentence commas ("zero, add") so the number before them converts; "10,20,30" lists stay intact.
        s = NumberWords(s.Replace(", ", " , ")).Replace(" , ", ", ");
        s = SpokenDot().Replace(s, ".");                  // "10 dot 0" → "10.0"
        s = SpokenSymbol().Replace(s, m => m.Groups["w"].Value.ToLowerInvariant() switch
        {
            "colon" => ":",
            "dash" or "hyphen" or "minus" => "-",
            "underscore" => "_",
            _ => "/",                                       // slash / forward slash
        });
        s = SpacedDigitSymbols().Replace(s, "$1");        // "10.0.0.1 / 24" → "10.0.0.1/24", "0 / 0" → "0/0"
        s = InterfaceName().Replace(s, m => $"{m.Groups["kw"].Value} {CanonicalInterface(m.Groups["name"].Value)}{m.Groups["num"].Value}");
        s = ConfigureTerminal().Replace(s, "configure terminal");
        return MultiSpace().Replace(s, " ").Trim();
    }

    [GeneratedRegex(@"\b(?:v|vee)\s+lan(?=s?\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SpokenVlan();

    [GeneratedRegex(@"\.\s+(?=[A-Za-z])")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"[\s.!?]+$")]
    private static partial Regex TrailingPunctuation();

    [GeneratedRegex(@"(?<=\d)\s+(?:dot|point|period)\s+(?=\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SpokenDot();

    [GeneratedRegex(@"\s*\b(?:forward\s+)?(?<w>slash|colon|dash|hyphen|underscore)\b\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SpokenSymbol();

    [GeneratedRegex(@"(?<=\d)\s*([./:])\s*(?=\d)")]
    private static partial Regex SpacedDigitSymbols();

    [GeneratedRegex(@"\b(?<kw>interface|int)\s+(?<name>ten\s*gig(?:abit)?(?:\s*ethernet)?|gig(?:abit)?\s*ethernet|gig|fast\s*ethernet|ethernet|loop\s*back|port[\s-]*channel|tunnel|vlan|management|mgmt)\s*(?<num>\d[\d/.:]*)", RegexOptions.IgnoreCase)]
    private static partial Regex InterfaceName();

    [GeneratedRegex(@"\b(?:configure\s+terminal|config(?:ure)?\s+t|conf\s+t|config(?:uration)?\s+mode|global\s+config(?:uration)?(?:\s+mode)?|configuration\s+terminal)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ConfigureTerminal();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    private static string CanonicalInterface(string spoken)
    {
        var k = new string(spoken.ToLowerInvariant().Where(char.IsLetter).ToArray());
        return k switch
        {
            _ when k.StartsWith("ten") => "TenGigabitEthernet",
            "gig" or "gigabit" or "gigethernet" or "gigabitethernet" => "GigabitEthernet",
            "fastethernet" => "FastEthernet",
            "ethernet" => "Ethernet",
            "loopback" => "Loopback",
            "portchannel" => "Port-channel",
            "tunnel" => "Tunnel",
            "vlan" => "Vlan",
            _ => "Management",
        };
    }

    // ---------- number words

    private static readonly Dictionary<string, int> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    /// <summary>
    /// Replaces runs of number words with digits, but only in numeric context - next to a digit, "dot", "slash", or
    /// after a keyword like "vlan"/"mask" - so "one" in "show one thing" or a hostname stays a word.
    /// "two hundred fifty five" → 255, "twenty four" → 24, and digit-by-digit "one nine two" → 192.
    /// </summary>
    private static string NumberWords(string s)
    {
        var tokens = s.Split(' ');
        var output = new List<string>();
        int i = 0;
        while (i < tokens.Length)
        {
            // Speech-to-text homophones: "interface loopback then" is "ten", "vlan to" is "two".
            var prevWord = output.Count > 0 ? output[^1] : "";
            // Only straight after an interface/VLAN-type name, and never when a number follows ("set the IP to 10.1.1.1").
            var nextWord = i + 1 < tokens.Length ? tokens[i + 1] : "";
            if (HomophoneContext().IsMatch(prevWord) && !(nextWord.Length > 0 && char.IsDigit(nextWord[0])) && Homophones.TryGetValue(tokens[i], out var digitWord))
                tokens[i] = digitWord;
            if (TryParseNumber(tokens, i, out var value, out int used))
            {
                var prev = output.Count > 0 ? output[^1] : "";
                var next = i + used < tokens.Length ? tokens[i + used] : "";
                bool tenGig = next.StartsWith("gig", StringComparison.OrdinalIgnoreCase); // "ten gig ethernet" is a name
                if (!tenGig && (IsNumericContext(prev) || IsNumberKeyword(prev) || IsNumericContext(next)))
                {
                    output.Add(value);
                    i += used;
                    continue;
                }
            }
            output.Add(tokens[i++]);
        }
        return string.Join(' ', output);
    }

    [GeneratedRegex(@"^(?:loopback|back|vlan|vlans|tunnel|channel|ethernet|gig|gigabit|area|vty|console)$", RegexOptions.IgnoreCase)]
    private static partial Regex HomophoneContext();

    private static readonly Dictionary<string, string> Homophones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["then"] = "ten", ["den"] = "ten", ["to"] = "two", ["too"] = "two", ["for"] = "four", ["fore"] = "four",
        ["won"] = "one", ["ate"] = "eight", ["tree"] = "three", ["free"] = "three", ["fife"] = "five", ["nein"] = "nine",
    };

    private static bool IsNumericContext(string t) =>
        t.Length > 0 && (char.IsDigit(t[0]) || t is "." or "/" or ":" || NumericWord().IsMatch(t));

    [GeneratedRegex(@"^(?:dot|point|period|slash|colon)$", RegexOptions.IgnoreCase)]
    private static partial Regex NumericWord();

    /// <summary>Words after which a lone number is clearly a number: "vlan ten", "tunnel one", "mask twenty four".</summary>
    private static bool IsNumberKeyword(string t) => NumberKeyword().IsMatch(t);

    [GeneratedRegex(@"^(?:vlan|vlans|tunnel|loopback|back|channel|port|ethernet|gig|gigabit|interface|int|area|as|process|id|number|priority|cost|mtu|metric|prefix|mask|length|seq|sequence|line|vty|console|address|ip|via|to|hop|ping|traceroute)$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberKeyword();

    private static readonly string[] MaskOctets = ["255", "254", "252", "248", "240", "224", "192", "128"];

    /// <summary>
    /// "...dot one two five five dot two five five..." - the last host octet and the first mask octet, spoken
    /// digit by digit, run together as "1255". A mask octet is almost always one of a handful of values, so split
    /// where the tail is one of them: 1255 → "1 255", 10255 → "10 255". Runs that are a valid octet stay whole.
    /// </summary>
    private static string SplitAtMaskBoundary(string digits)
    {
        if (digits.Length <= 3 && int.Parse(digits) <= 255) return digits;
        foreach (var mask in MaskOctets)
        {
            if (!digits.EndsWith(mask) || digits.Length == mask.Length) continue;
            var head = digits[..^mask.Length];
            if (head.Length <= 3 && int.Parse(head) <= 255) return $"{head} {mask}";
        }
        return digits;
    }

    private static bool TryParseNumber(string[] t, int start, out string value, out int used)
    {
        value = ""; used = 0;
        string W(int j) => j < t.Length ? t[j] : "";
        bool IsDigitWord(string w) => Units.TryGetValue(w, out var d) && d < 10;

        // Digit-by-digit: "one nine two" → 192 (how people read IP octets and ports aloud).
        if (IsDigitWord(W(start)) && IsDigitWord(W(start + 1)))
        {
            var sb = new StringBuilder();
            int j = start;
            while (IsDigitWord(W(j))) sb.Append(Units[W(j++)]);
            value = SplitAtMaskBoundary(sb.ToString());
            used = j - start;
            return true;
        }

        // Octet style: "one ninety two" → 192, "two fifty five" → 255, "one sixty eight" → 168, "two twelve" → 212.
        if (Units.TryGetValue(W(start), out var hundreds) && hundreds is > 0 and < 10)
        {
            int rest = -1, restUsed = 0;
            if (Tens.TryGetValue(W(start + 1), out var t2))
            {
                rest = t2; restUsed = 1;
                if (Units.TryGetValue(W(start + 2), out var u2) && u2 is > 0 and < 10) { rest += u2; restUsed = 2; }
            }
            else if (Units.TryGetValue(W(start + 1), out var teen) && teen >= 10) { rest = teen; restUsed = 1; }
            if (rest >= 0)
            {
                value = (hundreds * 100 + rest).ToString();
                used = 1 + restUsed;
                return true;
            }
        }

        int i = start, total = 0;
        bool any = false;
        if (Units.TryGetValue(W(i), out var h) && h is > 0 and < 10 && W(i + 1).Equals("hundred", StringComparison.OrdinalIgnoreCase))
        {
            total = h * 100; i += 2; any = true;
            if (W(i).Equals("and", StringComparison.OrdinalIgnoreCase) && (Tens.ContainsKey(W(i + 1)) || Units.ContainsKey(W(i + 1)))) i++;
        }
        if (Tens.TryGetValue(W(i), out var tens))
        {
            total += tens; i++; any = true;
            if (Units.TryGetValue(W(i), out var unit) && unit is > 0 and < 10) { total += unit; i++; }
        }
        else if (Units.TryGetValue(W(i), out var small))
        {
            total += small; i++; any = true;
        }
        if (!any) return false;
        value = total.ToString();
        used = i - start;
        return true;
    }
}
