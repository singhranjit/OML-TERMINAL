namespace OmlTerminal.Core.Voice;

/// <summary>
/// Cuts a live 16 kHz mono sample stream into utterances: speech starts when the level rises well above the
/// (continuously learned) background noise, and ends after a short silence. Each finished utterance is handed to the
/// transcriber in one piece - Whisper works on whole phrases, not a live stream. Also reports a 0..1 input level so
/// the UI can show a meter ("is it hearing me at all?").
/// </summary>
public sealed class UtteranceDetector
{
    public const int SampleRate = 16000;
    private const int FrameSamples = SampleRate / 50;          // 20 ms frames

    private readonly List<float> _utterance = new();
    private readonly Queue<float[]> _preRoll = new();         // keep the start of a word that began before detection
    private readonly float[] _frame = new float[FrameSamples];
    private int _frameFill;
    private double _noiseFloor = 0.005;
    private bool _speaking;
    private int _loudFrames, _silentFrames, _voicedFrames;

    /// <summary>How long a pause ends an utterance.</summary>
    public TimeSpan EndSilence { get; init; } = TimeSpan.FromMilliseconds(1200);
    public TimeSpan MaxUtterance { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Minimum amount of actual voice (not counting pauses) - filters out clicks, coughs and key taps.</summary>
    public TimeSpan MinVoiced { get; init; } = TimeSpan.FromMilliseconds(250);

    public event Action<float[]>? Utterance;
    /// <summary>0..1, roughly logarithmic, raised every 20 ms frame.</summary>
    public event Action<double>? Level;
    public event Action<bool>? SpeakingChanged;

    public bool IsSpeaking => _speaking;

    public void Push(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
        {
            _frame[_frameFill++] = s;
            if (_frameFill == FrameSamples) { OnFrame(); _frameFill = 0; }
        }
    }

    /// <summary>Force the current utterance out (the user pressed stop mid-sentence).</summary>
    public void Flush()
    {
        if (_speaking) Finish();
    }

    private void OnFrame()
    {
        double sum = 0;
        foreach (var s in _frame) sum += s * s;
        double rms = Math.Sqrt(sum / FrameSamples);
        Level?.Invoke(Math.Clamp((20 * Math.Log10(rms + 1e-9) + 60) / 60, 0, 1)); // -60 dBFS..0 → 0..1

        double threshold = Math.Max(_noiseFloor * 3.5, 0.012);
        bool loud = rms > threshold;
        var copy = (float[])_frame.Clone();

        if (!_speaking)
        {
            // Learn the room's background level while nobody is talking.
            if (!loud) _noiseFloor = _noiseFloor * 0.95 + rms * 0.05;
            _preRoll.Enqueue(copy);
            if (_preRoll.Count > 15) _preRoll.Dequeue();          // ~300 ms
            _loudFrames = loud ? _loudFrames + 1 : 0;
            if (_loudFrames >= 3)
            {
                _speaking = true;
                _silentFrames = 0;
                _voicedFrames = _loudFrames;
                foreach (var f in _preRoll) _utterance.AddRange(f);
                _preRoll.Clear();
                SpeakingChanged?.Invoke(true);
            }
            return;
        }

        _utterance.AddRange(copy);
        _silentFrames = loud ? 0 : _silentFrames + 1;
        if (loud) _voicedFrames++;
        if (_silentFrames * 20 >= EndSilence.TotalMilliseconds || _utterance.Count >= MaxUtterance.TotalSeconds * SampleRate) Finish();
    }

    private void Finish()
    {
        _speaking = false;
        _loudFrames = 0;
        var samples = _utterance.ToArray();
        _utterance.Clear();
        SpeakingChanged?.Invoke(false);
        var voiced = _voicedFrames * 20;
        _voicedFrames = 0;
        if (voiced >= MinVoiced.TotalMilliseconds) Utterance?.Invoke(samples);
    }
}
