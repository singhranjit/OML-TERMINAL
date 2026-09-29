using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using OmlTerminal.Core.Voice;

namespace OmlTerminal.Core.Tests;

/// <summary>
/// The full offline pipeline the Voice Command window runs on each utterance: command grammar and Whisper on the
/// same audio, combined by <see cref="HybridDecision"/>, parsed, validated. Covers the commands that failed live
/// (loopback, IP address + mask). Skipped without the Whisper model.
/// </summary>
public class HybridSpeechTests
{
    private static readonly Lazy<(GrammarRecognizer G, WhisperTranscriber W)?> Engines = new(() =>
        WhisperTranscriber.ModelInstalled && GrammarRecognizer.IsAvailable() ? (new GrammarRecognizer(), new WhisperTranscriber()) : null);

    private static float[] Say(string p)
    {
        using var wav = new MemoryStream();
        using (var synth = new SpeechSynthesizer())
        {
            synth.Rate = -1;
            synth.SetOutputToAudioStream(wav, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(p);
        }
        var pcm = wav.ToArray();
        var s = new float[pcm.Length / 2];
        for (int i = 0; i < s.Length; i++) s[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
        return s;
    }

    [Theory]
    [InlineData("interface loopback ten", new[] { "interface Loopback10" }, true)]
    [InlineData("IP address 192.168.1.1 255.255.255.0", new[] { "ip address 192.168.1.1 255.255.255.0" }, true)]
    [InlineData("IP address ten dot ten dot ten dot one slash thirty two", new[] { "ip address 10.10.10.1 255.255.255.255" }, true)]
    [InlineData("no shut", new[] { "no shutdown" }, true)]
    [InlineData("description uplink to core", new[] { "description uplink to core" }, true)]
    [InlineData("configure terminal and go to interface gigabit ethernet one slash zero slash two",
        new[] { "configure terminal", "interface GigabitEthernet1/0/2" }, true)]
    // Written-form digits the synthesizer reads in a way neither engine parses: allowed to be declined, never sent wrong.
    [InlineData("IP address 10.10.10.1 255.255.255.255", new[] { "ip address 10.10.10.1 255.255.255.255" }, false)]
    public async Task SpokenCommands_AreExactAndSafe_OrDeclined_NeverWrong(string spoken, string[] expected, bool mustSucceed)
    {
        if (Engines.Value is not var (g, w)) return;
        var audio = Say(spoken);
        var (grammarText, confidence) = g.Recognize(audio);
        var whisperText = await w.TranscribeAsync(audio);
        var d = HybridDecision.Choose(grammarText, confidence, whisperText);
        var ios = new CiscoIosGrammar();
        var cli = VoiceCommandParser.Expand(d.Text, ios);
        var trace = $"grammar \"{grammarText}\" ({confidence:0.00}), whisper \"{whisperText}\" → {d.Source}: [{string.Join(" | ", cli)}]";
        bool exact = expected.SequenceEqual(cli, StringComparer.OrdinalIgnoreCase);
        bool wouldAutoSend = d.Trusted && VoiceCommandParser.SafeToAutoSend(cli, ios);
        Assert.False(wouldAutoSend && !exact, "WRONG command would be sent: " + trace);
        if (mustSucceed) Assert.True(exact && wouldAutoSend, trace);
    }
}
