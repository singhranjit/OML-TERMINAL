using OmlTerminal.Core.Voice;

namespace OmlTerminal.Core.Tests;

public class VoiceSafetyTests
{
    private readonly CiscoIosGrammar _ios = new();

    private bool AutoSends(string heard) => VoiceCommandParser.SafeToAutoSend(VoiceCommandParser.Expand(heard, _ios), _ios);

    [Theory]
    // What actually reached a core switch in hands-free mode - must never be auto-sent again.
    [InlineData("Are there enough")]
    [InlineData("are")]
    [InlineData("It's a big in fig")]
    [InlineData("Create the land 20 and put it in veal and 20")]
    [InlineData("okay so what do I do now")]
    [InlineData("add ip address")]                                    // incomplete
    [InlineData("configure terminal and are there enough")]           // one bad line poisons the whole utterance
    [InlineData("")]
    public void MisheardOrIncompleteSpeech_IsNeverAutoSent(string heard) => Assert.False(AutoSends(heard));

    [Theory]
    [InlineData("configure terminal")]
    [InlineData("go to interface gigabit ethernet zero slash zero and add ip address 10.0.0.1/24 and bring it up")]
    [InlineData("create vlan 20 and name it USERS")]
    [InlineData("show ip interface brief")]
    [InlineData("save the config")]
    public void RecognizedCommands_AreAutoSent(string heard) => Assert.True(AutoSends(heard));

    [Fact]
    public void VendorVocabularyIsRespected()
    {
        var junos = new JuniperGrammar();
        Assert.True(VoiceCommandParser.SafeToAutoSend(["show interfaces terse"], junos));
        Assert.False(VoiceCommandParser.SafeToAutoSend(["switchport mode access"], junos));
        var forti = new FortinetGrammar();
        Assert.True(VoiceCommandParser.SafeToAutoSend(["get system status"], forti));
        Assert.False(VoiceCommandParser.SafeToAutoSend(["are there enough"], forti));
    }

    [Fact]
    public void WhisperNonSpeechTagsAreRemoved()
        => Assert.Equal("configure terminal", WhisperTranscriber.Clean(" [BLANK_AUDIO] configure terminal (keyboard clicking)"));
}

public class UtteranceDetectorTests
{
    private static float[] Tone(double seconds, float amplitude)
    {
        var n = (int)(seconds * UtteranceDetector.SampleRate);
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = amplitude * (float)Math.Sin(2 * Math.PI * 220 * i / UtteranceDetector.SampleRate);
        return s;
    }

    [Fact]
    public void SpeechBetweenSilences_IsOneUtterance()
    {
        var d = new UtteranceDetector();
        var got = new List<float[]>();
        d.Utterance += got.Add;
        d.Push(Tone(1.0, 0.001f));   // quiet room
        d.Push(Tone(1.5, 0.3f));     // speech
        d.Push(Tone(1.2, 0.001f));   // pause ends it
        var u = Assert.Single(got);
        Assert.InRange(u.Length / (double)UtteranceDetector.SampleRate, 1.5, 3.0);
    }

    [Fact]
    public void ClicksShorterThanMinimum_AreIgnored_AndSilenceProducesNothing()
    {
        var d = new UtteranceDetector();
        var got = new List<float[]>();
        d.Utterance += got.Add;
        d.Push(Tone(2.0, 0.001f));
        d.Push(Tone(0.08, 0.5f));    // a click
        d.Push(Tone(1.5, 0.001f));
        Assert.Empty(got);
    }

    [Fact]
    public void LevelMeterReportsLoudness()
    {
        var d = new UtteranceDetector();
        double last = 0;
        d.Level += l => last = l;
        d.Push(Tone(0.1, 0.001f));
        var quiet = last;
        d.Push(Tone(0.1, 0.5f));
        Assert.True(last > quiet + 0.3, $"quiet {quiet:0.00} loud {last:0.00}");
    }
}
