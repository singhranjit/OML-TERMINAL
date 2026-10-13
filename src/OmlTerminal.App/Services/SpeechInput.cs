using OmlTerminal.Core.Voice;
using OmlTerminal.Core.Voice.Assistant;

namespace OmlTerminal.App.Services;

/// <summary>The default Windows microphone at 16 kHz mono, for the voice assistant.</summary>
public sealed class NAudioCapture : IAudioCapture
{
    private NAudio.Wave.WaveIn? _mic;
    private volatile bool _stopping;

    public string Name => "Windows microphone";
    public event Action<float[]>? Samples;
    public event Action<string?>? Stopped;

    /// <summary>Null when Windows reports no recording device at all (common over Remote Desktop).</summary>
    public static NAudioCapture? Create() => NAudio.Wave.WaveIn.DeviceCount > 0 ? new NAudioCapture() : null;

    public void Start()
    {
        _stopping = false;
        _mic?.Dispose(); // restarting after a stop: release the previous capture device
        _mic = new NAudio.Wave.WaveIn
        {
            WaveFormat = new NAudio.Wave.WaveFormat(UtteranceDetector.SampleRate, 16, 1),
            BufferMilliseconds = 50,
        };
        _mic.DataAvailable += (_, e) =>
        {
            var samples = new float[e.BytesRecorded / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
            Samples?.Invoke(samples);
        };
        _mic.RecordingStopped += (_, e) => Stopped?.Invoke(_stopping ? null : e.Exception?.Message);
        _mic.StartRecording();
    }

    public void Stop()
    {
        _stopping = true;
        try { _mic?.StopRecording(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _mic?.Dispose();
        _mic = null;
    }
}
