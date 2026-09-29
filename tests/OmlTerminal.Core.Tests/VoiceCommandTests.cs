using OmlTerminal.Core.Voice;

namespace OmlTerminal.Core.Tests;

public class CidrHelperTests
{
    [Theory]
    [InlineData(24, "255.255.255.0")]
    [InlineData(16, "255.255.0.0")]
    [InlineData(8, "255.0.0.0")]
    [InlineData(32, "255.255.255.255")]
    [InlineData(0, "0.0.0.0")]
    [InlineData(30, "255.255.255.252")]
    public void KnownPrefixes(int prefix, string expected) => Assert.Equal(expected, CidrHelper.MaskFromPrefix(prefix));

    [Theory]
    [InlineData(-1)]
    [InlineData(33)]
    public void OutOfRangeReturnsNull(int prefix) => Assert.Null(CidrHelper.MaskFromPrefix(prefix));
}

public class CiscoIosGrammarTests
{
    private readonly CiscoIosGrammar _g = new();

    [Fact]
    public void CompoundCommandFromSpec_ExpandsToExactMultiLineSequence()
    {
        var result = VoiceCommandParser.Expand(
            "go to configure terminal and go to interface gi0/0 and add ip address 10.0.0.1/24 and no shutdown", _g);
        Assert.Equal(["configure terminal", "interface gi0/0", "ip address 10.0.0.1 255.255.255.0", "no shutdown"], result);
    }

    [Theory]
    [InlineData("go to configure terminal", "configure terminal")]
    [InlineData("GO TO Configure Terminal", "configure terminal")]
    [InlineData("enter interface gi0/1", "interface gi0/1")]
    [InlineData("add ip address 192.168.1.1/24", "ip address 192.168.1.1 255.255.255.0")]
    [InlineData("set ip address 192.168.1.1/30", "ip address 192.168.1.1 255.255.255.252")]
    [InlineData("add ip address 10.0.0.1 255.255.255.0", "ip address 10.0.0.1 255.255.255.0")]
    [InlineData("no shut", "no shutdown")]
    [InlineData("no shutdown", "no shutdown")]
    [InlineData("shut", "shutdown")]
    [InlineData("shutdown", "shutdown")]
    [InlineData("save config", "write memory")]
    [InlineData("save configuration", "write memory")]
    [InlineData("save the configuration", "write memory")]
    [InlineData("set hostname core-sw1", "hostname core-sw1")]
    [InlineData("set description Uplink to core", "description Uplink to core")]
    public void SingleTranslations(string phrase, string expected) => Assert.Equal(expected, _g.Translate(phrase));

    [Theory]
    [InlineData("show ip interface brief")]
    [InlineData("show version")]
    [InlineData("ping 10.0.0.1")]
    [InlineData("")]
    public void UnrecognizedPhrases_PassThroughUnchanged(string phrase) => Assert.Equal(phrase, _g.Translate(phrase));

    [Fact]
    public void ExpandSplitsOnAnd_CaseInsensitive_ExtraWhitespaceTolerated()
    {
        var result = VoiceCommandParser.Expand("  no shut   AND   shutdown  ", _g);
        Assert.Equal(["no shutdown", "shutdown"], result);
    }

    [Fact]
    public void Expand_EmptyOrWhitespaceInput_ReturnsEmpty()
    {
        Assert.Empty(VoiceCommandParser.Expand("", _g));
        Assert.Empty(VoiceCommandParser.Expand("   ", _g));
    }

    [Fact]
    public void InvalidCidrPrefix_FallsBackToVerbatimPhrase()
        => Assert.Equal("add ip address 10.0.0.1/99", _g.Translate("add ip address 10.0.0.1/99"));

    [Fact]
    public void WordAndInsideAnUnrelatedPhraseIsNotTreatedAsASeparator()
        // "and" only splits phrases when surrounded by whitespace as its own word; this checks the parser doesn't
        // split mid-word (e.g. a hostname containing "and" as a substring, like "brand").
        => Assert.Equal(["hostname brand-new-switch"], VoiceCommandParser.Expand("set hostname brand-new-switch", _g));
}

public class OtherGrammarsTests
{
    [Fact]
    public void Nxos_UsesCopyRunningConfigInsteadOfWriteMemory()
    {
        var g = new CiscoNxosGrammar();
        Assert.Equal("copy running-config startup-config", g.Translate("save config"));
        Assert.Equal("interface eth1/1", g.Translate("go to interface eth1/1"));
    }

    [Fact]
    public void Asa_InheritsIosRulesUnchanged()
        => Assert.Equal("no shutdown", new CiscoAsaGrammar().Translate("no shut"));

    [Fact]
    public void AristaEos_InheritsIosRulesUnchanged()
        => Assert.Equal("ip address 10.0.0.1 255.255.255.0", new AristaEosGrammar().Translate("add ip address 10.0.0.1/24"));

    [Theory]
    [InlineData(typeof(JuniperGrammar))]
    [InlineData(typeof(FortinetGrammar))]
    [InlineData(typeof(PaloAltoGrammar))]
    [InlineData(typeof(GenericGrammar))]
    public void SkeletonGrammars_HandleGoTo_AndPassThroughEverythingElse(Type grammarType)
    {
        var g = (IVendorGrammar)Activator.CreateInstance(grammarType)!;
        Assert.Equal("configure", g.Translate("go to configure"));
        Assert.Equal("show interfaces terse", g.Translate("show interfaces terse"));
    }

    [Fact]
    public void EveryGrammarHasADistinctName()
    {
        IVendorGrammar[] all = [new CiscoIosGrammar(), new CiscoNxosGrammar(), new CiscoAsaGrammar(),
            new AristaEosGrammar(), new JuniperGrammar(), new FortinetGrammar(), new PaloAltoGrammar(), new GenericGrammar()];
        Assert.Equal(all.Length, all.Select(g => g.Name).Distinct().Count());
    }
}
