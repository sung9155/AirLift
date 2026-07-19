using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AirLift;

/// <summary>
/// Owns one streaming session: RAOP client + audio capture + paced send thread.
/// </summary>
public sealed class StreamEngine : IDisposable
{
    private const int PacketBytes = AlacEncoder.FramesPerPacket * AlacEncoder.BytesPerFrame; // 1408
    private const double PacketInterval = (double)AlacEncoder.FramesPerPacket / 44100.0;     // ~7.98 ms
    private const double MaxRateTrim = 0.005;    // +/-0.5% send-rate servo range

    private double _targetBufferMs = 150;        // standing capture buffer (absorbs clock skew)

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private RaopClient? _client;
    private AudioCapture? _capture;
    private Thread? _sendThread;
    private CancellationTokenSource? _cts;

    public SpeakerInfo? Speaker { get; private set; }
    public string? CaptureDeviceName => _capture?.DeviceName;
    public bool IsConnected => _client != null;

    /// <summary>Raised from a background thread when the stream dies unexpectedly.</summary>
    public event Action<string>? Error;

    /// <summary>Current output level 0..1 (peak, smoothed) for UI meters.</summary>
    public float Level => _level;
    private volatile float _level;

    /// <param name="latencyMode">"Ultra": 40ms buffer + 100ms offset, "Low": 60+150, "Stable": 150+250.</param>
    public async Task StartAsync(SpeakerInfo speaker, string? captureDeviceId, double volumeDb, string latencyMode)
    {
        await StopAsync();

        int? latencyOverride; // frames @44.1kHz; null = device-reported (11025)
        (_targetBufferMs, latencyOverride) = latencyMode switch
        {
            "Ultra" => (40.0, (int?)4410),
            "Stable" => (150.0, null),
            _ => (60.0, 6615),
        };

        var client = new RaopClient(speaker.Ip, speaker.Port, speaker.Encrypt, speaker.AuthSetup, latencyOverride);
        client.Log += msg => Debug.WriteLine($"[raop] {msg}");
        client.Fatal += msg => OnStreamError(msg);
        try
        {
            await client.ConnectAsync(volumeDb);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        AudioCapture capture;
        try
        {
            capture = new AudioCapture(captureDeviceId);
            capture.Start();
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        _capture = capture;
        Speaker = speaker;
        _cts = new CancellationTokenSource();

        _sendThread = new Thread(() => SendLoop(client, capture, _cts.Token))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "raop-send",
        };
        _sendThread.Start();
    }

    private void SendLoop(RaopClient client, AudioCapture capture, CancellationToken ct)
    {
        var pcm = new byte[PacketBytes];
        int peak = 0;
        double lastStats = 0;
        int silencePackets = 0, clears = 0;
        string statsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AirLift", "stream.log");

        timeBeginPeriod(1); // 1 ms sleep granularity - default 15.6 ms causes packet bursts
        try
        {
            // Prefill a standing buffer so clock skew doesn't starve reads immediately
            var prefill = Stopwatch.StartNew();
            while (capture.BufferedDuration.TotalMilliseconds < _targetBufferMs &&
                   prefill.ElapsedMilliseconds < 1500 && !ct.IsCancellationRequested)
                Thread.Sleep(5);

            var sw = Stopwatch.StartNew();
            double next = 0;

            while (!ct.IsCancellationRequested)
            {
                // Rate servo: lock the send rate to the capture clock by nudging the
                // packet interval based on buffer level. Buffer above target -> send
                // slightly faster, below -> slightly slower. Receiver absorbs the
                // tiny rate deviation via its interpolation.
                double bufferedMs = capture.BufferedDuration.TotalMilliseconds;
                double trim = Math.Clamp((bufferedMs - _targetBufferMs) / _targetBufferMs, -1, 1) * MaxRateTrim;
                next += PacketInterval * (1 - trim);

                double wait = next - sw.Elapsed.TotalSeconds;
                while (wait > 0.0015)
                {
                    Thread.Sleep(1);
                    if (ct.IsCancellationRequested) return;
                    wait = next - sw.Elapsed.TotalSeconds;
                }
                while (sw.Elapsed.TotalSeconds < next && !ct.IsCancellationRequested) { }

                // If we fell far behind (system sleep, debugger pause), resynchronize
                if (sw.Elapsed.TotalSeconds - next > 0.5)
                    next = sw.Elapsed.TotalSeconds;

                bufferedMs = capture.BufferedDuration.TotalMilliseconds;
                if (bufferedMs >= PacketInterval * 1000)
                {
                    capture.Read(pcm, PacketBytes);
                }
                else
                {
                    // Not enough captured audio: send a full silence packet and leave
                    // the partial data queued - padding real audio mid-packet crackles.
                    Array.Clear(pcm);
                    silencePackets++;
                }

                // Gross anomaly (device change, long stall): drop back to target
                if (bufferedMs > 600)
                {
                    capture.Clear();
                    clears++;
                }

                int packetPeak = 0;
                for (int i = 0; i < pcm.Length; i += 2)
                {
                    int v = Math.Abs((int)(short)(pcm[i] | (pcm[i + 1] << 8)));
                    if (v > packetPeak) packetPeak = v;
                }
                if (packetPeak > peak) peak = packetPeak;
                // Smoothed level for the tray meter: instant attack, ~0.3s decay
                _level = Math.Max(packetPeak / 32768f, _level * 0.97f);
                if (sw.Elapsed.TotalSeconds - lastStats >= 5)
                {
                    lastStats = sw.Elapsed.TotalSeconds;
                    // Write off-thread: a synchronous disk flush here stalls packet pacing
                    string line =
                        $"{DateTime.Now:HH:mm:ss} sent={client.PacketsSent} timingReq={client.TimingRequestsReceived} " +
                        $"resendReq={client.ResendRequestsReceived} buf={bufferedMs:0}ms silence={silencePackets} clears={clears} " +
                        $"peak={(peak == 0 ? "SILENT" : $"{20 * Math.Log10(peak / 32768.0):0.0}dBFS")}\r\n";
                    _ = Task.Run(() => { try { File.AppendAllText(statsPath, line); } catch { } });
                    peak = 0;
                }

                try
                {
                    client.SendAudioPacket(pcm);
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        OnStreamError(L.StreamError(ex.Message));
                    return;
                }
            }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    private void OnStreamError(string message)
    {
        // Tear down from a worker thread, then notify
        _ = Task.Run(async () =>
        {
            try { await StopAsync(); } catch { }
            Error?.Invoke(message);
        });
    }

    public async Task SetVolumeAsync(double db)
    {
        var client = _client;
        if (client != null)
            await client.SetVolumeAsync(db);
    }

    public async Task StopAsync()
    {
        var client = _client;
        var capture = _capture;
        var cts = _cts;
        var thread = _sendThread;
        _client = null;
        _capture = null;
        _cts = null;
        _sendThread = null;
        Speaker = null;

        _level = 0;
        if (cts != null) cts.Cancel();
        if (thread != null && thread.IsAlive) thread.Join(1000);
        if (client != null)
        {
            try { await client.TeardownAsync(); } catch { }
            client.Dispose();
        }
        capture?.Dispose();
        cts?.Dispose();
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
