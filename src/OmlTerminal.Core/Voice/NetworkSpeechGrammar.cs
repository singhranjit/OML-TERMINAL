using System.Runtime.Versioning;
using Sapi = System.Speech.Recognition;

namespace OmlTerminal.Core.Voice;

/// <summary>
/// A speech grammar of what network engineers actually say at a CLI - modes, interfaces, IP addresses, VLANs, routes,
/// show commands - chained with "and"/"then". Giving the offline recognizer this vocabulary is the difference between
/// "create VLAN twenty" and "create the land 20". Numbers are the spoken forms engineers use: "ten", "twenty four",
/// "two hundred fifty five", and octet style "one ninety two" / "two fifty five". The recognized text is then
/// normalized and translated by <see cref="VoiceCommandParser"/> exactly like typed input.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NetworkSpeechGrammar
{
    public const string Name = "network-commands";

    private static readonly Lazy<string[]> Numbers = new(() => NumberPhrases(0, 255).ToArray());

    // Every element is created fresh at each use: System.Speech mis-compiles a GrammarBuilder or Choices instance
    // that appears in more than one place ("'' rule reference not defined in this grammar").
    private static Sapi.Choices Num() => new(Numbers.Value);
    private static Sapi.Choices Dot() => new("dot", "point");
    private static Sapi.GrammarBuilder Slash() => new("slash");
    private static Sapi.GrammarBuilder Ip() => Seq(Num(), Dot(), Num(), Dot(), Num(), Dot(), Num());
    private static Sapi.GrammarBuilder Optional(params string[] words) => new(new Sapi.Choices(words), 0, 1);

    /// <summary>"VLAN" isn't a dictionary word, so the recognizer can't guess how it sounds - spell out how people say it.</summary>
    private static readonly string[] VlanSpoken = ["vlan", "v lan", "vee lan"];

    private static Sapi.Choices Vlan(params string[] leads) =>
        new(leads.SelectMany(l => VlanSpoken.Select(v => l.Replace("{vlan}", v))).ToArray());

    private static Sapi.Choices Phrases()
    {
        var prefixOrMask = new Sapi.Choices(
            Seq(Slash(), Num()),
            Ip(),                                                         // "10.1.1.1 255.255.255.0" - mask straight after
            Seq(new Sapi.Choices("mask", "subnet mask", "netmask", "with mask", "with subnet mask", "with a mask of", "mask of"), Ip()));

        var ifNum = new Sapi.GrammarBuilder(Num());
        ifNum.Append(Seq(Slash(), Num()), 0, 2);

        return new Sapi.Choices(
            Seq(Optional("go to", "enter", "switch to"), new Sapi.Choices("configure terminal", "config mode", "configuration mode",
                "global config", "privileged mode", "enable mode", "enable")),
            new Sapi.GrammarBuilder(new Sapi.Choices("exit", "end", "go back", "exit config mode", "leave config mode",
                "no shutdown", "no shut", "shutdown", "bring it up", "bring the interface up", "enable the interface",
                "shut it down", "disable the interface", "bring it down",
                "save the config", "save the configuration", "save config", "save", "write memory", "copy run start",
                "make it an access port", "make it a trunk", "switchport mode access", "switchport mode trunk",
                "get ip from dhcp", "ip address dhcp")),
            Seq(Optional("go to", "enter", "switch to"), new Sapi.GrammarBuilder("interface"),
                new Sapi.Choices("gigabit ethernet", "gig ethernet", "gig", "fast ethernet", "ten gig", "ten gigabit ethernet",
                    "ethernet", "loopback", "vlan", "v lan", "vee lan", "port channel", "tunnel", "management"), ifNum),
            Seq(Optional("add", "set", "assign", "configure", "give it"), new Sapi.Choices("ip address", "ip", "the ip address", "an ip address"),
                Ip(), prefixOrMask),
            Seq(Vlan("create {vlan}", "create a {vlan}", "add {vlan}", "{vlan}", "make {vlan}"), Num()),
            Seq(Vlan("put it in {vlan}", "access {vlan}", "assign it to {vlan}", "switchport access {vlan}"), Num()),
            Seq(new Sapi.Choices("add a default route via", "default route via", "default gateway", "add default route via"), Ip()),
            Seq(new Sapi.Choices("add a route to", "route to", "static route to"), Ip(), Slash(), Num(), new Sapi.Choices("via", "next hop"), Ip()),
            Seq(new Sapi.Choices("ping", "traceroute"), Ip()),
            // Free text (names, hostnames, descriptions) is left to the dictation grammar loaded alongside.
            Seq(new Sapi.GrammarBuilder("show"), Optional("me the", "the"),
                new Sapi.Choices("running config", "run", "ip interface brief", "interfaces brief", "interface brief",
                    "interfaces status", "vlan brief", "v lan brief", "vlans", "v lans", "version", "ip route", "routing table", "cdp neighbors",
                    "neighbors", "ip bgp summary", "ip ospf neighbor", "spanning tree", "mac address table", "logging",
                    "clock", "users", "inventory", "ip arp", "arp", "interfaces", "startup config")));
    }

    public static Sapi.Grammar Build()
    {
        // One command = optional joiner/filler + a phrase, repeated. The phrase list must appear exactly once:
        // System.Speech can't compile a grammar holding two copies of it, but repeating one copy works.
        var command = Seq(Optional("and", "then", "and then", "please", "now", "ok", "okay", "can you"), Phrases());
        var utterance = new Sapi.GrammarBuilder(command, 1, 9);
        utterance.Append(Optional("please"));
        return new Sapi.Grammar(utterance) { Name = Name, Priority = 1 };
    }

    private static Sapi.GrammarBuilder Seq(params object[] parts)
    {
        var gb = new Sapi.GrammarBuilder();
        foreach (var p in parts)
        {
            switch (p)
            {
                case string s: gb.Append(s); break;
                case Sapi.Choices c: gb.Append(c); break;
                case Sapi.GrammarBuilder g: gb.Append(g); break;
            }
        }
        return gb;
    }

    private static readonly string[] Ones =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
         "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];

    private static readonly string[] TensWords = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    private static string UpTo99(int n) =>
        n < 20 ? Ones[n] : TensWords[n / 10] + (n % 10 == 0 ? "" : " " + Ones[n % 10]);

    /// <summary>Every way the numbers from..to are commonly spoken (distinct phrases).</summary>
    public static IEnumerable<string> NumberPhrases(int from, int to)
    {
        var seen = new HashSet<string>();
        for (int n = from; n <= to; n++)
        {
            if (n < 100) { if (seen.Add(UpTo99(n))) yield return UpTo99(n); continue; }
            int h = n / 100, rest = n % 100;
            var formal = Ones[h] + " hundred" + (rest == 0 ? "" : " " + UpTo99(rest));
            if (seen.Add(formal)) yield return formal;
            if (rest != 0)
            {
                var withAnd = Ones[h] + " hundred and " + UpTo99(rest);
                if (seen.Add(withAnd)) yield return withAnd;
            }
            if (rest >= 10)
            {
                var octet = Ones[h] + " " + UpTo99(rest); // "one ninety two", "two fifty five"
                if (seen.Add(octet)) yield return octet;
            }
            // Digit by digit: "two five five", "one nine two", "one oh one"
            var digits = string.Join(' ', n.ToString().Select(c => Ones[c - '0']));
            if (seen.Add(digits)) yield return digits;
            if (n.ToString().Contains('0'))
            {
                var oh = string.Join(' ', n.ToString().Select(c => c == '0' ? "oh" : Ones[c - '0']));
                if (seen.Add(oh)) yield return oh;
            }
        }
    }
}
