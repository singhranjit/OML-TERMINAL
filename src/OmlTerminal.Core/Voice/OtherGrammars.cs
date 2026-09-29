using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Voice;

/// <summary>NX-OS shares almost all basic IOS syntax; the one common difference is that "write memory" is deprecated in favor of "copy running-config startup-config".</summary>
public sealed class CiscoNxosGrammar : CiscoIosGrammar
{
    public override string Name => "Cisco NX-OS";

    public override string Translate(string phrase)
    {
        var translated = base.Translate(phrase);
        return translated == "write memory" ? "copy running-config startup-config" : translated;
    }
}

/// <summary>Cisco ASA basic config syntax matches classic IOS for most shorthands; the interface summary differs.</summary>
public sealed class CiscoAsaGrammar : CiscoIosGrammar
{
    public override string Name => "Cisco ASA";

    public override string Translate(string phrase)
    {
        var translated = base.Translate(phrase);
        return translated == "show ip interface brief" ? "show interface ip brief" : translated;
    }
}

/// <summary>
/// Starting skeleton only: Junos's set-based, commit-driven config model doesn't map onto the Cisco-style
/// shorthands above, so this grammar recognizes just "go to X" (dropping filler words) and otherwise passes
/// spoken text through unchanged. Deeper Junos-specific phrases (interface/address/commit shorthands) are not
/// implemented yet.
/// </summary>
public partial class JuniperGrammar : IVendorGrammar
{
    public virtual string Name => "Juniper Junos";

    [GeneratedRegex(@"^(?:go\s+to|enter)\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GoToRegex();

    public virtual bool LooksLikeCli(string line) => VoiceCli.Junos().IsMatch(line.Trim());

    public virtual string Translate(string phrase)
    {
        phrase = phrase.Trim();
        var m = GoToRegex().Match(phrase);
        return m.Success ? m.Groups[1].Value.Trim() : phrase;
    }
}

/// <summary>Starting skeleton only - see JuniperGrammar. Arista EOS is largely IOS-like for basic commands, so this inherits the Cisco IOS ruleset as a reasonable first approximation.</summary>
public sealed class AristaEosGrammar : CiscoIosGrammar
{
    public override string Name => "Arista EOS";
}

/// <summary>Starting skeleton only: FortiOS's CLI (config/edit/set/next/end) doesn't map onto the Cisco-style shorthands, so only "go to X" filler-word dropping is implemented.</summary>
public sealed class FortinetGrammar : JuniperGrammar
{
    public override string Name => "Fortinet FortiOS";
    public override bool LooksLikeCli(string line) => VoiceCli.FortiOs().IsMatch(line.Trim());
}

/// <summary>Starting skeleton only: PAN-OS's CLI doesn't map onto the Cisco-style shorthands, so only "go to X" filler-word dropping is implemented.</summary>
public sealed class PaloAltoGrammar : JuniperGrammar
{
    public override string Name => "Palo Alto PAN-OS";
    public override bool LooksLikeCli(string line) => VoiceCli.PanOs().IsMatch(line.Trim());
}

/// <summary>
/// Works for any vendor: only drops "go to X" filler words and otherwise sends exactly what was typed, verbatim.
/// This is what makes the voice-command feature usable on gear with no dedicated grammar - the moment you just
/// say/type the device's real CLI syntax, it goes straight through untouched, for every vendor.
/// </summary>
public sealed class GenericGrammar : JuniperGrammar
{
    public override string Name => "Generic - any vendor (type exact CLI)";
    public override bool LooksLikeCli(string line) => VoiceCli.Generic().IsMatch(line.Trim());
}
