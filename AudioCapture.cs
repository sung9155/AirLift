using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AirLift;

/// <summary>
/// Captures audio played to a render (output) device via WASAPI loopback and
/// delivers it as 44.1 kHz / 16-bit / stereo PCM through a pull interface.
/// Uses a fully managed conversion chain (WdlResampler) so consumption from the
/// source buffer is smooth and its level is a usable clock-drift signal.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private readonly WasapiLoopbackCapture _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly IWaveProvider _output;

    public string DeviceName { get; }

    /// <param name="deviceId">MMDevice ID of the render device to capture, or null for the default device.</param>
    public AudioCapture(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = deviceId == null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(deviceId);
        DeviceName = device.FriendlyName;

        _capture = new WasapiLoopbackCapture(device);
        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true,
            ReadFully = true, // return silence on underrun so the chain never stalls
        };
        _capture.DataAvailable += (_, e) => _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        ISampleProvider samples = _buffer.ToSampleProvider();
        if (samples.WaveFormat.Channels != 2)
            samples = new ToStereoProvider(samples);
        var resampled = new WdlResamplingSampleProvider(samples, 44100);
        _output = new SampleToWaveProvider16(resampled);
    }

    public void Start() => _capture.StartRecording();

    /// <summary>Duration of captured audio waiting in the source buffer.</summary>
    public TimeSpan BufferedDuration => _buffer.BufferedDuration;

    /// <summary>Fills the buffer with converted PCM; pads with silence if not enough data.</summary>
    public void Read(byte[] destination, int count)
    {
        int read = _output.Read(destination, 0, count);
        if (read < count)
            Array.Clear(destination, read, count - read);
    }

    /// <summary>Drops all buffered audio (gross drift recovery).</summary>
    public void Clear() => _buffer.ClearBuffer();

    /// <summary>Active render devices as (id, friendly name).</summary>
    public static List<(string Id, string Name)> GetRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => (d.ID, d.FriendlyName))
            .OrderBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Finds an installed virtual cable render device (VB-CABLE "CABLE Input"), or null.</summary>
    public static (string Id, string Name)? FindVirtualCable()
    {
        foreach (var d in GetRenderDevices())
        {
            if (d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
                d.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
                return d;
        }
        return null;
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
    }

    /// <summary>Maps mono or multichannel float samples to stereo (ch0/ch1, mono duplicated).</summary>
    private sealed class ToStereoProvider(ISampleProvider source) : ISampleProvider
    {
        private readonly int _srcChannels = source.WaveFormat.Channels;
        private float[] _tmp = Array.Empty<float>();

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int needed = frames * _srcChannels;
            if (_tmp.Length < needed) _tmp = new float[needed];
            int gotFrames = source.Read(_tmp, 0, needed) / _srcChannels;
            for (int f = 0; f < gotFrames; f++)
            {
                buffer[offset + f * 2] = _tmp[f * _srcChannels];
                buffer[offset + f * 2 + 1] = _tmp[f * _srcChannels + (_srcChannels >= 2 ? 1 : 0)];
            }
            return gotFrames * 2;
        }
    }
}
