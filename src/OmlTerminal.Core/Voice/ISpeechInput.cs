namespace OmlTerminal.Core.Voice;

/// <summary>A speech-to-text source for the Voice Command dialog. Events fire on background threads.</summary>
public interface ISpeechInput : IDisposable
{
    string EngineName { get; }
    event Action<string>? Hypothesis;
    /// <summary>Final text, and whether it came from a reliable source (Whisper, or the network command grammar)
    /// rather than free dictation. Hands-free mode never sends untrusted results.</summary>
    event Action<string, bool>? Recognized;
    /// <summary>Microphone input level 0..1, for the meter. Not every engine reports it.</summary>
    event Action<double>? Level;
    /// <summary>Raised when listening ends by itself; the argument is an error message, or null.</summary>
    event Action<string?>? Stopped;
    Task StartAsync();
    Task StopAsync();
}
