using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AirLift;

/// <summary>
/// RAOP (AirPlay v1 audio) sender: RTSP session management plus RTP audio,
/// control (sync/retransmit) and timing channels.
/// </summary>
public sealed class RaopClient : IDisposable
{
    // Public RSA key of AirPlay receivers (from AirPort Express firmware, used by all RAOP senders)
    private const string RsaModulusB64 =
        "59dE8qLieItsH1WgjrcFRKj6eUWqi+bGLOX1HL3U3GhC/j0Qg90u3sG/1CUtwC5vOYvfDmFI6oSF" +
        "Xi5ELabWJmT2dKHzBJKa3k9ok+8t9ucRqMd6DZHJ2YCCLlDRKSKv6kDqnw4UwPdpOMXziC/AMj3Z" +
        "/lUVX1G7WSHCAWKf1zNS1eLvqr+boEjXuBOitnZ/bDzPHrTOZz0Dew0uowxf/+sG+NCK3eQJVxqc" +
        "aJ/vEHKIVd2M+5qL71yJQ+87X6oV3eaYvt3zWZYD6z5vYTcrtij2VZ9Zmni/UAaHqn9JdsBWLUEp" +
        "VviYnhimNVvYFZeCXg/IdTQ+x4IRdiXNv5hEew==";
    private const string RsaExponentB64 = "AQAB";

    private const int SampleRate = 44100;
    private const int FramesPerPacket = AlacEncoder.FramesPerPacket;
    private const int ResendBufferSize = 512;

    private readonly IPAddress _deviceAddress;
    private readonly int _rtspPort;
    private readonly bool _encrypt;
    private readonly bool _authSetup;
    private readonly int? _latencyOverride;

    private TcpClient? _rtspTcp;
    private NetworkStream? _rtspStream;
    private readonly SemaphoreSlim _rtspLock = new(1, 1);
    private int _cseq;
    private string _sessionUrl = "";
    private string? _rtspSessionId;
    private readonly string _clientInstance;
    private readonly string _dacpId;
    private readonly string _activeRemote;

    private UdpClient? _audioSocket;
    private UdpClient? _controlSocket;
    private UdpClient? _timingSocket;
    private IPEndPoint? _audioEndpoint;
    private IPEndPoint? _controlEndpoint;

    private ushort _seq;
    private uint _rtpTime;
    private readonly uint _ssrc;
    private bool _firstPacket = true;
    private int _packetsSinceSync;
    private int _latency = 11025;

    private readonly byte[] _aesKey = new byte[16];
    private readonly byte[] _aesIv = new byte[16];
    private readonly Aes _aes;

    // Ring buffer of recently sent RTP packets for retransmission requests
    private readonly byte[]?[] _resendBuffer = new byte[]?[ResendBufferSize];
    private readonly object _resendLock = new();

    private readonly CancellationTokenSource _cts = new();

    public int Latency => _latency;
    public event Action<string>? Log;
    /// <summary>Raised when the RTSP session dies (keep-alive failure).</summary>
    public event Action<string>? Fatal;

    // Diagnostics counters
    public long PacketsSent;
    public long TimingRequestsReceived;
    public long ResendRequestsReceived;

    public RaopClient(IPAddress deviceAddress, int rtspPort, bool encrypt, bool authSetup = false, int? latencyOverride = null)
    {
        _deviceAddress = deviceAddress;
        _rtspPort = rtspPort;
        _encrypt = encrypt;
        _authSetup = authSetup;
        _latencyOverride = latencyOverride;

        var rng = RandomNumberGenerator.Create();
        rng.GetBytes(_aesKey);
        rng.GetBytes(_aesIv);

        var rnd = new Random();
        _seq = (ushort)rnd.Next(0, ushort.MaxValue);
        _rtpTime = (uint)rnd.NextInt64(0, uint.MaxValue);
        _ssrc = (uint)rnd.NextInt64(0, uint.MaxValue);
        _clientInstance = RandomHex(rnd, 16);
        _dacpId = RandomHex(rnd, 16);
        _activeRemote = rnd.Next(100000000, int.MaxValue).ToString();

        _aes = Aes.Create();
        _aes.Mode = CipherMode.CBC;
        _aes.Padding = PaddingMode.None;
        _aes.Key = _aesKey;
    }

    private static string RandomHex(Random rnd, int digits)
    {
        var sb = new StringBuilder(digits);
        for (int i = 0; i < digits; i++) sb.Append("0123456789ABCDEF"[rnd.Next(16)]);
        return sb.ToString();
    }

    public uint StartRtpTime => _rtpTime;

    public async Task ConnectAsync(double initialVolumeDb)
    {
        _rtspTcp = new TcpClient();
        await _rtspTcp.ConnectAsync(_deviceAddress, _rtspPort, _cts.Token);
        _rtspTcp.ReceiveTimeout = 10000;
        _rtspStream = _rtspTcp.GetStream();
        _rtspStream.ReadTimeout = 10000;

        var localIp = ((IPEndPoint)_rtspTcp.Client.LocalEndPoint!).Address;
        var sid = (uint)new Random().NextInt64(0, uint.MaxValue);
        _sessionUrl = $"rtsp://{localIp}/{sid}";

        // Local UDP sockets: audio sender + control + timing listeners
        _audioSocket = new UdpClient(0);
        _controlSocket = new UdpClient(0);
        _timingSocket = new UdpClient(0);
        int controlPort = ((IPEndPoint)_controlSocket.Client.LocalEndPoint!).Port;
        int timingPort = ((IPEndPoint)_timingSocket.Client.LocalEndPoint!).Port;

        await SendRtspAsync("OPTIONS", "*", new());

        // MFi auth-setup handshake (devices advertising et=4). The receiver only
        // needs a valid Curve25519 public key; the response is ignored.
        if (_authSetup)
        {
            var gen = new Org.BouncyCastle.Crypto.Generators.X25519KeyPairGenerator();
            gen.Init(new Org.BouncyCastle.Crypto.Parameters.X25519KeyGenerationParameters(
                new Org.BouncyCastle.Security.SecureRandom()));
            var keyPair = gen.GenerateKeyPair();
            var pubKey = ((Org.BouncyCastle.Crypto.Parameters.X25519PublicKeyParameters)keyPair.Public).GetEncoded();
            var body = new byte[33];
            body[0] = 0x01; // unencrypted transport
            pubKey.CopyTo(body, 1);
            var authResponse = await SendRtspAsync("POST", "/auth-setup", new()
            {
                ["Content-Type"] = "application/octet-stream",
            }, body);
            EnsureOk(authResponse, "auth-setup");
            Log?.Invoke("auth-setup OK");
        }

        // ANNOUNCE
        var sdp = new StringBuilder();
        sdp.Append("v=0\r\n");
        sdp.Append($"o=iTunes {sid} 0 IN IP4 {localIp}\r\n");
        sdp.Append("s=iTunes\r\n");
        sdp.Append($"c=IN IP4 {_deviceAddress}\r\n");
        sdp.Append("t=0 0\r\n");
        sdp.Append("m=audio 0 RTP/AVP 96\r\n");
        sdp.Append("a=rtpmap:96 AppleLossless\r\n");
        sdp.Append($"a=fmtp:96 {FramesPerPacket} 0 16 40 10 14 2 255 0 0 {SampleRate}\r\n");
        if (_encrypt)
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = Convert.FromBase64String(RsaModulusB64),
                Exponent = Convert.FromBase64String(RsaExponentB64),
            });
            var encryptedKey = rsa.Encrypt(_aesKey, RSAEncryptionPadding.OaepSHA1);
            sdp.Append($"a=rsaaeskey:{Convert.ToBase64String(encryptedKey).TrimEnd('=')}\r\n");
            sdp.Append($"a=aesiv:{Convert.ToBase64String(_aesIv).TrimEnd('=')}\r\n");
        }

        var response = await SendRtspAsync("ANNOUNCE", _sessionUrl, new()
        {
            ["Content-Type"] = "application/sdp",
        }, Encoding.ASCII.GetBytes(sdp.ToString()));
        EnsureOk(response, "ANNOUNCE");

        // Listeners must run before SETUP: some receivers probe our timing port
        // while processing SETUP and won't answer until they get a reply.
        _ = Task.Run(() => TimingLoopAsync(_cts.Token));
        _ = Task.Run(() => ControlLoopAsync(_cts.Token));

        // SETUP
        response = await SendRtspAsync("SETUP", _sessionUrl, new()
        {
            ["Transport"] = $"RTP/AVP/UDP;unicast;mode=record;control_port={controlPort};timing_port={timingPort}",
        });
        EnsureOk(response, "SETUP");
        _rtspSessionId = response.Headers.GetValueOrDefault("Session")?.Split(';')[0].Trim();

        var transport = response.Headers.GetValueOrDefault("Transport") ?? "";
        int serverPort = ParseTransportPort(transport, "server_port") ?? 6000;
        int remoteControlPort = ParseTransportPort(transport, "control_port") ?? (serverPort + 1);
        _audioEndpoint = new IPEndPoint(_deviceAddress, serverPort);
        _controlEndpoint = new IPEndPoint(_deviceAddress, remoteControlPort);

        // RECORD
        response = await SendRtspAsync("RECORD", _sessionUrl, new()
        {
            ["Range"] = "npt=0-",
            ["RTP-Info"] = $"seq={_seq};rtptime={_rtpTime}",
        });
        EnsureOk(response, "RECORD");
        if (response.Headers.TryGetValue("Audio-Latency", out var latencyStr) &&
            int.TryParse(latencyStr, out var lat) && lat > 0)
        {
            _latency = lat;
        }
        // Playback offset is driven by our sync packets, so we can override the
        // device-reported value to trade jitter margin for lower latency.
        if (_latencyOverride is { } overrideFrames)
            _latency = overrideFrames;

        await SetVolumeAsync(initialVolumeDb);

        // RTSP keep-alive
        _ = Task.Run(() => KeepAliveLoopAsync(_cts.Token));

        Log?.Invoke($"Connected. audio->{_audioEndpoint} control->{_controlEndpoint} latency={_latency}");
    }

    private static int? ParseTransportPort(string transport, string key)
    {
        foreach (var part in transport.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim() == key && int.TryParse(kv[1].Trim(), out var p))
                return p;
        }
        return null;
    }

    /// <summary>Encodes, encrypts and sends one packet of PCM (1408 bytes, 16-bit LE stereo).</summary>
    public void SendAudioPacket(ReadOnlySpan<byte> pcm)
    {
        if (_audioSocket == null || _audioEndpoint == null) throw new InvalidOperationException("Not connected");

        // Sync packet: at start and roughly once per second
        if (_firstPacket || _packetsSinceSync >= 126)
        {
            SendSyncPacket(_firstPacket);
            _packetsSinceSync = 0;
        }

        var alac = AlacEncoder.Encode(pcm);
        var payload = _encrypt ? EncryptPayload(alac) : alac;

        var packet = new byte[12 + payload.Length];
        packet[0] = 0x80;
        packet[1] = _firstPacket ? (byte)0xE0 : (byte)0x60;
        packet[2] = (byte)(_seq >> 8);
        packet[3] = (byte)_seq;
        WriteUInt32(packet, 4, _rtpTime);
        WriteUInt32(packet, 8, _ssrc);
        payload.CopyTo(packet.AsSpan(12));

        _audioSocket.Send(packet, packet.Length, _audioEndpoint);
        Interlocked.Increment(ref PacketsSent);

        lock (_resendLock)
            _resendBuffer[_seq % ResendBufferSize] = packet;

        _seq++;
        _rtpTime += (uint)FramesPerPacket;
        _firstPacket = false;
        _packetsSinceSync++;
    }

    private byte[] EncryptPayload(byte[] alac)
    {
        // AES-128-CBC, IV reset per packet, only whole 16-byte blocks encrypted
        var result = (byte[])alac.Clone();
        int encryptable = alac.Length & ~0xF;
        if (encryptable > 0)
        {
            using var enc = _aes.CreateEncryptor(_aesKey, _aesIv);
            enc.TransformBlock(alac, 0, encryptable, result, 0);
        }
        return result;
    }

    private void SendSyncPacket(bool first)
    {
        if (_controlSocket == null || _controlEndpoint == null) return;
        var packet = new byte[20];
        packet[0] = first ? (byte)0x90 : (byte)0x80;
        packet[1] = 0xD4;
        packet[2] = 0x00;
        packet[3] = 0x07;
        WriteUInt32(packet, 4, _rtpTime - (uint)_latency);
        NtpTime.WriteBigEndian(packet, 8, NtpTime.Now());
        WriteUInt32(packet, 16, _rtpTime);
        try { _controlSocket.Send(packet, packet.Length, _controlEndpoint); }
        catch { /* transient send errors are non-fatal */ }
    }

    private async Task TimingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _timingSocket!.ReceiveAsync(ct);
                var req = result.Buffer;
                if (req.Length < 32 || (req[1] & 0x7F) != 0x52) continue;
                Interlocked.Increment(ref TimingRequestsReceived);

                var reply = new byte[32];
                reply[0] = 0x80;
                reply[1] = 0xD3;
                reply[2] = req[2];
                reply[3] = req[3];
                Array.Copy(req, 24, reply, 8, 8); // origin = request transmit time
                ulong now = NtpTime.Now();
                NtpTime.WriteBigEndian(reply, 16, now); // receive
                NtpTime.WriteBigEndian(reply, 24, now); // transmit
                await _timingSocket.SendAsync(reply, reply.Length, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { /* ignore malformed packets */ }
        }
    }

    private async Task ControlLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _controlSocket!.ReceiveAsync(ct);
                var req = result.Buffer;
                // Retransmit request: type 0x55
                if (req.Length >= 8 && (req[1] & 0x7F) == 0x55)
                {
                    Interlocked.Increment(ref ResendRequestsReceived);
                    ushort firstSeq = (ushort)((req[4] << 8) | req[5]);
                    ushort count = (ushort)((req[6] << 8) | req[7]);
                    for (int i = 0; i < count && i < ResendBufferSize; i++)
                    {
                        ushort wanted = (ushort)(firstSeq + i);
                        byte[]? original;
                        lock (_resendLock)
                            original = _resendBuffer[wanted % ResendBufferSize];
                        if (original == null) continue;
                        ushort storedSeq = (ushort)((original[2] << 8) | original[3]);
                        if (storedSeq != wanted) continue;

                        var resend = new byte[4 + original.Length];
                        resend[0] = 0x80;
                        resend[1] = 0xD6;
                        resend[2] = req[2];
                        resend[3] = req[3];
                        original.CopyTo(resend, 4);
                        await _controlSocket.SendAsync(resend, resend.Length, _controlEndpoint);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { /* ignore */ }
        }
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                await SendRtspAsync("OPTIONS", "*", new());
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"Keep-alive failed: {ex.Message}");
                Fatal?.Invoke(L.SessionLost(ex.Message));
                break;
            }
        }
    }

    /// <summary>Volume in dB: 0 (max) .. -30 (min), -144 = mute.</summary>
    public async Task SetVolumeAsync(double db)
    {
        var body = Encoding.ASCII.GetBytes($"volume: {db:0.000000}\r\n");
        var response = await SendRtspAsync("SET_PARAMETER", _sessionUrl, new()
        {
            ["Content-Type"] = "text/parameters",
        }, body);
        EnsureOk(response, "SET_PARAMETER volume");
    }

    public async Task TeardownAsync()
    {
        try { await SendRtspAsync("TEARDOWN", _sessionUrl, new()); }
        catch { /* connection may already be gone */ }
    }

    private sealed record RtspResponse(int StatusCode, string StatusLine, Dictionary<string, string> Headers, byte[] Body);

    private static void EnsureOk(RtspResponse response, string what)
    {
        if (response.StatusCode == 401 || response.StatusCode == 403)
            throw new InvalidOperationException(L.PasswordRequired(what));
        if (response.StatusCode != 200)
            throw new InvalidOperationException($"{what} failed: {response.StatusLine}");
    }

    private async Task<RtspResponse> SendRtspAsync(string method, string url, Dictionary<string, string> headers, byte[]? body = null)
    {
        await _rtspLock.WaitAsync();
        try
        {
            int cseq = ++_cseq;
            var sb = new StringBuilder();
            sb.Append($"{method} {url} RTSP/1.0\r\n");
            sb.Append($"CSeq: {cseq}\r\n");
            sb.Append("User-Agent: iTunes/11.0.4 (Windows; N)\r\n");
            sb.Append($"Client-Instance: {_clientInstance}\r\n");
            sb.Append($"DACP-ID: {_dacpId}\r\n");
            sb.Append($"Active-Remote: {_activeRemote}\r\n");
            if (_rtspSessionId != null)
                sb.Append($"Session: {_rtspSessionId}\r\n");
            foreach (var (k, v) in headers)
                sb.Append($"{k}: {v}\r\n");
            if (body != null)
                sb.Append($"Content-Length: {body.Length}\r\n");
            sb.Append("\r\n");

            var head = Encoding.ASCII.GetBytes(sb.ToString());
            await _rtspStream!.WriteAsync(head);
            if (body != null)
                await _rtspStream.WriteAsync(body);
            await _rtspStream.FlushAsync();

            var response = await ReadResponseAsync();
            Log?.Invoke($"{method} -> {response.StatusLine}");
            return response;
        }
        finally
        {
            _rtspLock.Release();
        }
    }

    private async Task<RtspResponse> ReadResponseAsync()
    {
        // NetworkStream.ReadTimeout does not apply to async reads - use a CTS
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var headerBytes = new List<byte>(512);
        var single = new byte[1];
        // Read until CRLFCRLF
        while (true)
        {
            int n = await _rtspStream!.ReadAsync(single.AsMemory(0, 1), timeout.Token);
            if (n == 0) throw new IOException("RTSP connection closed by device");
            headerBytes.Add(single[0]);
            int c = headerBytes.Count;
            if (c >= 4 && headerBytes[c - 4] == '\r' && headerBytes[c - 3] == '\n' &&
                headerBytes[c - 2] == '\r' && headerBytes[c - 1] == '\n')
                break;
            if (c > 65536) throw new IOException("RTSP response header too large");
        }

        var text = Encoding.ASCII.GetString(headerBytes.ToArray());
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var statusLine = lines[0];
        int statusCode = 0;
        var statusParts = statusLine.Split(' ');
        if (statusParts.Length >= 2) int.TryParse(statusParts[1], out statusCode);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int idx = line.IndexOf(':');
            if (idx > 0)
                headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        byte[] responseBody = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var lenStr) &&
            int.TryParse(lenStr, out var len) && len > 0)
        {
            responseBody = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = await _rtspStream!.ReadAsync(responseBody.AsMemory(read, len - read), timeout.Token);
                if (n == 0) throw new IOException("RTSP connection closed mid-body");
                read += n;
            }
        }

        return new RtspResponse(statusCode, statusLine, headers, responseBody);
    }

    private static void WriteUInt32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _audioSocket?.Dispose();
        _controlSocket?.Dispose();
        _timingSocket?.Dispose();
        _rtspStream?.Dispose();
        _rtspTcp?.Dispose();
        _aes.Dispose();
        _cts.Dispose();
    }
}
