using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using OmlTerminal.Core.Voice;

namespace OmlTerminal.Core.Tests;

/// <summary>
/// End-to-end with the real Whisper model: Windows reads a command aloud (16 kHz mono WAV), Whisper transcribes it
/// offline, the parser turns it into CLI, and the hands-free safety check must approve it. Skipped silently when the
/// model hasn't been downloaded (it's ~148 MB and fetched on demand by the app).
/// </summary>
public class WhisperSpeechTests
{
    private static readonly Lazy<WhisperTranscriber?> Transcriber = new(() =>
        WhisperTranscriber.ModelInstalled ? new WhisperTranscriber() : null);

    private static async Task<string> HearAsync(string spoken)
    {
        using var wav = new MemoryStream();
        using (var synth = new SpeechSynthesizer())
        {
            synth.SetOutputToAudioStream(wav, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(spoken);
        }
        // SetOutputToAudioStream writes raw PCM; convert to floats like the microphone path does.
        var pcm = wav.ToArray();
        var samples = new float[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
        return await Transcriber.Value!.TranscribeAsync(samples);
    }

    [Theory]
    [InlineData("Go to configure terminal and then go to interface gigabit ethernet zero slash zero",
        new[] { "configure terminal", "interface GigabitEthernet0/0" })]
    [InlineData("Add IP address ten dot zero dot zero dot one slash twenty four and bring it up",
        new[] { "ip address 10.0.0.1 255.255.255.0", "no shutdown" })]
    [InlineData("Create VLAN twenty and name it users",
        new[] { "vlan 20", "name users" })]
    [InlineData("Save the config", new[] { "write memory" })]
    [InlineData("Show IP interface brief", new[] { "show ip interface brief" })]
    [InlineData("Add a default route via one ninety two dot one sixty eight dot one dot two fifty four",
        new[] { "ip route 0.0.0.0 0.0.0.0 192.168.1.254" })]
    public async Task SpokenCommand_IsTranscribedByWhisperAndSafeToSend(string spoken, string[] expected)
    {
        if (Transcriber.Value is null) return;
        var heard = await HearAsync(spoken);
        var ios = new CiscoIosGrammar();
        var cli = VoiceCommandParser.Expand(heard, ios);
        Assert.True(expected.SequenceEqual(cli, StringComparer.OrdinalIgnoreCase), $"heard \"{heard}\" → [{string.Join(" | ", cli)}]");
        Assert.True(VoiceCommandParser.SafeToAutoSend(cli, ios), $"not safe to auto-send: [{string.Join(" | ", cli)}]");
    }
}
