using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using OmlTerminal.Core.Voice;

namespace OmlTerminal.Core.Tests;

public class CliValidatorTests
{
    [Theory]
    [InlineData("ip address 10.10.10.1 255.255.255.0")]
    [InlineData("ip address 10.10.10.1 255.255.255.255")]
    [InlineData("ip address dhcp")]
    [InlineData("interface Loopback10")]
    [InlineData("interface GigabitEthernet1/0/2")]
    [InlineData("interface gi0/1")]
    [InlineData("interface Port-channel1")]
    [InlineData("vlan 20")]
    [InlineData("ip route 0.0.0.0 0.0.0.0 10.0.0.1")]
    [InlineData("show ip interface brief")]
    [InlineData("no shutdown")]
    public void ValidCommands(string line) => Assert.Null(CliValidator.Problem(line));

    [Theory]
    [InlineData("ip address 10.10.10.1255.255.2555.255.255.255")]   // Whisper's glued address+mask
    [InlineData("ip address 10.10.10.10.1 subnet mask 255.255.255.0")]
    [InlineData("ip address 10.10.10.1 255.255.0.255")]              // non-contiguous mask
    [InlineData("ip address 10.10.10.1")]                            // no mask
    [InlineData("IP address")]                                       // what reached the switch
    [InlineData("interface loop back then")]                         // what reached the switch
    [InlineData("interface")]
    [InlineData("vlan 5000")]
    [InlineData("ip route 10.0.0.0 255.0.0.0")]
    public void InvalidCommands(string line) => Assert.NotNull(CliValidator.Problem(line));
}

public class VoiceAccuracyTests
{
    private readonly CiscoIosGrammar _ios = new();
    private IReadOnlyList<string> Cli(string heard) => VoiceCommandParser.Expand(heard, _ios);

    [Theory]
    [InlineData("interface loop back then", "interface Loopback10")]          // the real mishearing
    [InlineData("go to interface loop back 100", "interface Loopback100")]
    [InlineData("interface vlan to", "interface Vlan2")]
    [InlineData("no shot", "no shutdown")]
    [InlineData("no shoot", "no shutdown")]
    public void HomophonesAreRepaired(string heard, string expected) => Assert.Equal([expected], Cli(heard));

    [Fact]
    public void WhisperRepeats_AreSentOnce()
        => Assert.Equal(["interface Loopback10"], Cli(WhisperTranscriber.Clean("interface loop back 10. interface loop back 10.")));

    [Fact]
    public void DigitByDigitAddressAndMask_SplitAtTheMaskBoundary()
        => Assert.Equal(["ip address 192.168.1.1 255.255.255.0"],
            Cli("ip address one nine two point one six eight point one point one two five five point two five five point two five five point zero"));

    [Fact]
    public void GluedWhisperAddress_IsNotSafeToSend()
    {
        var lines = Cli("ip address 10.10.10.1255.255.2555.255.255.255.");
        Assert.False(VoiceCommandParser.SafeToAutoSend(lines, _ios));
    }

    [Theory]
    [InlineData("IP address", true)]
    [InlineData("configure terminal and interface Loopback10 and ip address", true)]
    [InlineData("go to interface", true)]
    [InlineData("ip address 10.1.1.1 255.255.255.0", false)]
    [InlineData("are there enough", false)]
    public void PausesMidCommandAreDetected(string heard, bool waits)
        => Assert.Equal(waits, VoiceCommandParser.EndsMidCommand(Cli(heard)));

    [Fact]
    public void PausedCommand_CompletesWhenTheRestArrives()
    {
        var joined = Cli("IP address" + " " + "10.10.10.1 255.255.255.255");
        Assert.Equal(["ip address 10.10.10.1 255.255.255.255"], joined);
        Assert.True(VoiceCommandParser.SafeToAutoSend(joined, _ios));
    }

    [Fact]
    public void Hybrid_TrustsGrammarWhenWhisperAgreesOnTheAddress()
    {
        var d = HybridDecision.Choose("ip address ten dot ten dot ten dot one slash thirty two", 0.94f, "ip address 10.10.10.1/32.");
        Assert.True(d.Trusted);
        Assert.Equal("grammar+whisper", d.Source);
    }

    [Fact]
    public void Hybrid_DistrustsGrammarWhenAddressesDisagree()
    {
        // The grammar confidently heard 104.104.104.1 where Whisper heard 10.10.10.1 - a human must check.
        var d = HybridDecision.Choose("ip address one zero four dot one zero four dot one zero four dot one two five five point two five five point two five five point two five five",
            0.85f, "ip address 10.10.10.1255.255.2555.255.255.255.");
        Assert.False(d.Trusted);
        Assert.NotNull(d.Note);
    }

    [Fact]
    public void Hybrid_FallsBackToWhisperOutsideTheGrammar()
    {
        var d = HybridDecision.Choose(null, 0, "description uplink to core");
        Assert.Equal(("description uplink to core", "whisper"), (d.Text, d.Source));
    }
}

/// <summary>The grammar recognizer on recorded buffers - the same path each microphone utterance takes.</summary>
public class GrammarRecognizerTests
{
    private static float[] Say(string p)
    {
        using var wav = new MemoryStream();
        using (var synth = new SpeechSynthesizer())
        {
            synth.Rate = -1; // dictation pace - people say config commands deliberately
            synth.SetOutputToAudioStream(wav, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(p);
        }
        var pcm = wav.ToArray();
        var s = new float[pcm.Length / 2];
        for (int i = 0; i < s.Length; i++) s[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
        return s;
    }

    [Theory]
    [InlineData("interface loopback ten", new[] { "interface Loopback10" })]
    [InlineData("IP address ten dot ten dot ten dot one slash thirty two", new[] { "ip address 10.10.10.1 255.255.255.255" })]
    [InlineData("IP address 192.168.1.1 255.255.255.0", new[] { "ip address 192.168.1.1 255.255.255.0" })]
    [InlineData("no shut", new[] { "no shutdown" })]
    [InlineData("Add IP address ten dot zero dot zero dot one slash twenty four and bring it up",
        new[] { "ip address 10.0.0.1 255.255.255.0", "no shutdown" })]
    [InlineData("Create VLAN twenty and put it in VLAN twenty", new[] { "vlan 20", "switchport access vlan 20" })]
    public void InGrammarCommands_AreExact(string spoken, string[] expected)
    {
        if (!GrammarRecognizer.IsAvailable()) return;
        using var g = new GrammarRecognizer();
        var (text, conf) = g.Recognize(Say(spoken));
        Assert.True(conf >= HybridDecision.GrammarThreshold, $"conf {conf} for \"{text}\"");
        Assert.Equal(expected, VoiceCommandParser.Expand(text!, new CiscoIosGrammar()));
    }

    /// <summary>Long chains may not match the grammar at all - that's fine (Whisper transcribes them); what must
    /// never happen is a confident match that's wrong.</summary>
    [Fact]
    public void LongChains_AreExactOrLeftToWhisper()
    {
        if (!GrammarRecognizer.IsAvailable()) return;
        using var g = new GrammarRecognizer();
        var (text, conf) = g.Recognize(Say("configure terminal and go to interface gigabit ethernet one slash zero slash two"));
        if (text is null || conf < HybridDecision.GrammarThreshold) return;
        Assert.Equal(["configure terminal", "interface GigabitEthernet1/0/2"], VoiceCommandParser.Expand(text, new CiscoIosGrammar()));
    }

    [Theory]
    [InlineData("are there enough")]
    [InlineData("description uplink to core")]
    [InlineData("what is the weather like today")]
    public void OutOfGrammarSpeech_IsRejected(string spoken)
    {
        if (!GrammarRecognizer.IsAvailable()) return;
        using var g = new GrammarRecognizer();
        var (text, conf) = g.Recognize(Say(spoken));
        Assert.True(text is null || conf < HybridDecision.GrammarThreshold, $"accepted \"{text}\" at {conf}");
    }
}
