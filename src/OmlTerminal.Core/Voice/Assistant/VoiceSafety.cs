using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Voice.Assistant;

public enum VoiceRisk
{
    /// <summary>Shows or navigates only - runs straight away.</summary>
    ReadOnly,
    /// <summary>Changes configuration or state - runs after the engineer says yes (or clicks).</summary>
    Change,
    /// <summary>Can take a device or service down, or wipe data - needs an explicit click, never voice alone.</summary>
    Destructive,
}

/// <summary>
/// Decides what the assistant may do without asking. Default-deny: only commands recognized as read-only or pure
/// CLI navigation run on their own; everything else is a change; a short list of outage/wipe commands is destructive.
/// The model never makes this call - it only proposes.
/// </summary>
public static partial class VoiceSafety
{
    public static VoiceRisk Classify(VoiceAction action) => action.Kind == VoiceActionKind.Run ? Classify(action.Value) : VoiceRisk.ReadOnly;

    public static VoiceRisk Classify(string command)
    {
        var c = Regex.Replace((command ?? "").Trim(), @"\s+", " ");
        if (c.Length == 0) return VoiceRisk.ReadOnly;
        if (Destructive().IsMatch(c)) return VoiceRisk.Destructive;
        if (Navigation().IsMatch(c)) return VoiceRisk.ReadOnly;
        var body = DoPrefix().Replace(c, "");
        // Display filters after a pipe ("| include up", "| grep eth0") are fine; anything else that chains,
        // redirects or substitutes makes the whole line a change, whatever it starts with.
        int pipe = body.IndexOf('|');
        if (pipe >= 0)
        {
            if (!body[(pipe + 1)..].Split('|').All(f => DisplayFilter().IsMatch(f.Trim()))) return VoiceRisk.Change;
            body = body[..pipe].TrimEnd();
        }
        if (body.IndexOfAny([';', '&', '`', '>', '<']) >= 0 || body.Contains("$(")) return VoiceRisk.Change;
        return ReadOnlyCommand().IsMatch(body) && !WritesFile().IsMatch(body) ? VoiceRisk.ReadOnly : VoiceRisk.Change;
    }

    public static VoiceRisk Highest(IEnumerable<VoiceAction> actions) =>
        actions.Select(Classify).DefaultIfEmpty(VoiceRisk.ReadOnly).Max();

    [GeneratedRegex(@"^(?:reload|reboot|halt|poweroff|shutdown\s+-[hrP]|init\s+[06]|write\s+erase|erase\b|format\b|delete\s+(?:/force\s+)?(?:flash|bootflash|nvram|disk|startup|slot)|request\s+system\s+(?:reboot|halt|power-off|zeroize|software)|execute\s+(?:reboot|shutdown|factoryreset|formatlogdisk|restore)|factory-?reset|reset\s+saved-configuration|startup\s+saved-configuration|clear\s+(?:ip\s+)?bgp\s+\*|clear\s+(?:arp|mac\s+address-table)\b(?!.*\b(?:interface|address)\b)|no\s+router\b|no\s+(?:interface\s+)?vlan\b|no\s+feature\b|crypto\s+key\s+zeroize|rm\s+-[rf]|mkfs|dd\s|system\s+reboot|/system\s+reset)", RegexOptions.IgnoreCase)]
    private static partial Regex Destructive();

    [GeneratedRegex(@"^(?:end|top|up|exit\s+configuration-mode|configure(?:\s+terminal)?|conf\s+t|config(?:ure)?\s+private|terminal\s+(?:length|width|pager)\s+\d+|terminal\s+monitor|terminal\s+no\s+monitor|set\s+cli\s+screen-(?:length|width)\s+\d+|enable|system-view|quit|return|screen-length\s+0\s+temporary)$", RegexOptions.IgnoreCase)]
    private static partial Regex Navigation();

    [GeneratedRegex(@"^do\s+", RegexOptions.IgnoreCase)]
    private static partial Regex DoPrefix();

    // Display filters after a pipe on network CLIs, and read-only Unix text tools.
    [GeneratedRegex(@"^(?:include|inc|i|exclude|exc|ex|e|begin|beg|b|section|sec|s|count|match|except|find|last|no-more|json|grep|egrep|head|tail|wc|sort|uniq|less|more|cut|column)(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayFilter();

    // Commands that only look: show/display/get on network gear, ping/trace, Unix and Windows inspection commands,
    // "ip addr" / "ip route" without add/del, MikroTik "... print".
    [GeneratedRegex(@"^(?:hostname|date|w|ifconfig|iwconfig|id)$|^(?:show|sho|display|dis|disp|get|ping|ping6|traceroute|traceroute6|trace|tracert|tracepath|mtr|nslookup|dig|host|whois|who|uptime|whoami|uname|pwd|ls|ll|dir|cat|head|tail|less|more|grep|egrep|wc|df|du|free|top|htop|ps|netstat|ss|arp|ipconfig|iw\s+dev\s+\S+\s+(?:info|link|station\s+dump)|nmcli\s+(?:dev|device|con|connection)\s+(?:show|status|list)|history|lsblk|lscpu|lsusb|lspci|dmesg|journalctl|systemctl\s+status|file\s+show|monitor\s+interface|test\s+(?:security-policy-match|nat-policy-match|routing\s+fib-lookup)|diagnose\s+(?:sys\s+(?:top|session\s+list|status|ha\s+status)|hardware\s+deviceinfo|netlink\s+interface\s+list|ip\s+arp\s+list)|execute\s+(?:ping|traceroute)|route\s+print|route\s+-n|get-\S+|test-connection|test-netconnection|resolve-dnsname)(?:\s.*)?$|^ip\s+(?:-\S+\s+)*(?:a|addr|address|link|l|route|r|neigh|n)(?:\s+show)?(?:\s+dev\s+\S+)?$|^/?[\w/ -]*\bprint(?:\s+[\w=-]+)*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReadOnlyCommand();

    // "show tech redirect flash:x", "show run | tee ..." write files.
    [GeneratedRegex(@"\b(?:redirect|tee|append)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WritesFile();
}

/// <summary>
/// Spoken confirmations in the languages engineers actually use - "yes", "go ahead", "haan", "sí", "oui", "ja",
/// "da", "hai", "shi de", "네", "na'am" ... - so "say yes to apply" works whatever language the request was in.
/// Matching is whole-word after removing accents and punctuation; anything not clearly yes or no is neither.
/// </summary>
public static class VoiceReplyWords
{
    private static readonly string[] Yes =
    [
        // English
        "yes", "yeah", "yep", "yup", "sure", "ok", "okay", "confirm", "confirmed", "go ahead", "do it", "apply", "apply it",
        "proceed", "affirmative", "correct", "right", "go", "send it", "run it", "please do",
        // Hindi / Urdu / Punjabi / Bengali / Tamil / Telugu / Marathi
        "haan", "han", "haa", "ha", "ji haan", "ji", "theek hai", "thik hai", "kar do", "karo", "chalo", "hanji", "haanji", "acha", "achha",
        "हाँ", "हां", "जी", "ठीक है", "कर दो", "ہاں", "جی", "হ্যাঁ", "ஆம்", "అవును", "हो",
        // European
        "si", "sí", "claro", "dale", "vale", "adelante", "oui", "d'accord", "vas-y", "ja", "jawohl", "genau", "mach", "sim", "pode",
        "certo", "da", "da da", "так", "tak", "ano", "igen", "evet", "tamam", "ναι", "nai", "kyllä", "joo", "ja visst", "javel",
        "да", "давай", "конечно", "так точно", "добре",
        // Middle East / Africa
        "naam", "na'am", "aywa", "نعم", "ايوه", "أيوه", "بله", "baleh", "areh", "כן", "ken", "ndiyo", "ndio", "eeh", "yebo", "ewe",
        // East / South-East Asia
        "hai", "はい", "ええ", "うん", "是", "是的", "对", "好", "好的", "可以", "shi", "dui", "hao", "네", "예", "응",
        "vâng", "dạ", "vang", "ya", "iya", "betul", "oo", "opo", "ใช่", "ครับ", "ค่ะ", "chai",
    ];

    private static readonly string[] No =
    [
        "no", "nope", "nah", "cancel", "stop", "don't", "dont", "do not", "abort", "never mind", "nevermind", "wait", "hold on", "negative", "not", "ne",
        "nahin", "nahi", "na", "mat karo", "ruko", "नहीं", "मत करो", "रुको", "نہیں", "না", "இல்லை", "కాదు",
        "non", "nein", "nicht", "não", "nao", "nee", "nej", "ei", "nem", "hayır", "hayir", "όχι", "ochi", "нет", "ні", "nie", "ne ne",
        "la", "لا", "na'", "نه", "לא", "lo", "hapana", "cha",
        "iie", "いいえ", "いや", "不", "不要", "不是", "取消", "bu", "buyao", "아니", "아니요", "아니오", "không", "khong", "tidak", "bukan", "hindi", "ไม่",
    ];

    public static bool IsYes(string utterance) => Matches(utterance, Yes) && !Matches(utterance, No);

    public static bool IsNo(string utterance) => Matches(utterance, No);

    private static bool Matches(string utterance, string[] words)
    {
        var u = Normalize(utterance);
        if (u.Length == 0 || u.Length > 60) return false; // a long sentence is a new request, not a yes/no
        foreach (var w in words)
        {
            var n = Normalize(w);
            if (n.Length == 0) continue;
            if (u == n || u.StartsWith(n + " ") || u.EndsWith(" " + n) || u.Contains(" " + n + " ")) return true;
            // Scripts written without spaces (Japanese, Chinese, Thai): a short reply that starts with the word.
            if (!n.Any(char.IsAsciiLetter) && u.StartsWith(n) && u.Length <= n.Length + 6) return true;
        }
        return false;
    }

    private static string Normalize(string s)
    {
        var d = (s ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark && ch < 0x0900) continue; // drop Latin accents only
            sb.Append(char.IsPunctuation(ch) && ch != '\'' ? ' ' : ch);
        }
        return Regex.Replace(sb.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
    }
}
