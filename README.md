# AirLift

Stream Windows system audio to AirPlay (RAOP) speakers. Lives in the system tray.

[한국어 문서 (Korean)](README.ko.md)

## Features

- **mDNS discovery** of AirPlay speakers, one-click connect from the tray menu
- **Three latency modes**: ultra-low ~0.15 s / low ~0.25 s / stable ~0.45 s
- **Auto-reconnect** with backoff when the stream drops
- **Virtual output device workflow**: with [VB-CABLE](https://vb-audio.com/Cable/) installed,
  AirLift auto-switches the Windows default output on connect and restores it on
  disconnect — sound goes to AirPlay only, not the local speakers
- **Live level meter** in the tray icon
- Receiver volume control / mute, Korean & English UI, run-at-startup option

## Install

Grab `AirLift.msi` from [Releases](../../releases) — self-contained (no .NET runtime
required), adds a Start Menu shortcut and the required inbound UDP firewall rule
(the receiver sends NTP timing and retransmit requests back to the sender; without
the rule there is no audio).

Or build from source:

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## Usage

1. Run AirLift → icon appears in the system tray
2. Right-click → **Speakers** → pick one; streaming starts
3. Double-click the icon to toggle connect/disconnect with the last speaker

For AirPlay-only output (silent local speakers) install VB-CABLE once; AirLift
detects it and handles the default-device switching automatically. Without it,
AirLift mirrors the default playback device (local + AirPlay simultaneously).

## Device compatibility

| TXT `et` value | Meaning | Supported |
|---|---|---|
| `0` | no encryption | yes |
| `1` | RSA/AES (AirPort Express, shairport-sync) | yes |
| `4` | MFi/FairPlay — `auth-setup` handshake, cleartext stream | yes (auto-detected) |
| `3`, `5` | FairPlay v2 / HomeKit pairing (Apple TV, HomePod) | no |

Password-protected speakers are not supported. Codec is ALAC only (the common
denominator for AirPlay v1 receivers).

## How it works

1. **Capture** — WASAPI loopback on the selected render device
2. **Convert** — managed resampler chain to 44.1 kHz / 16-bit / stereo PCM
3. **Send** — RAOP (AirPlay v1 audio):
   - RTSP handshake: `OPTIONS` → (`POST /auth-setup`) → `ANNOUNCE` → `SETUP` → `RECORD`
   - ALAC uncompressed-escape framing, 352 frames per packet
   - RTP/UDP audio + control (sync/retransmit) + timing (NTP) channels
   - AES-128-CBC payload encryption with RSA-OAEP key exchange where supported

### Implementation notes (hard-won)

- **ALAC frames need the 3-bit END tag (value 7)** — shairport-sync 5.x decodes with
  ffmpeg, which rejects frames without it (`no end tag found`). Legacy RAOP senders
  (raop_play, node_airtunes) omit it and play silence on modern receivers.
- **Start the timing/control UDP listeners before `SETUP`** — some receivers
  (e.g. EDIFIER p20 firmware) probe the sender's timing port while processing SETUP
  and never answer if nobody replies.
- **Pacing**: `timeBeginPeriod(1)` for 1 ms sleep granularity (the 15.6 ms default
  causes packet bursts), a standing capture buffer with a ±0.5 % send-rate servo to
  lock the send clock to the capture clock, full-silence packets instead of
  padding partial reads, and no synchronous file I/O on the send thread.
- The playback offset is driven by the sender's sync packets, so it can be set
  below the device-reported latency — that is what the ultra-low mode does.

## Project layout

| File | Role |
|---|---|
| `Program.cs` | entry point, single-instance mutex, CLI diagnostics |
| `TrayApp.cs` | tray icon, menus, connection orchestration |
| `StreamEngine.cs` | session owner: RaopClient + AudioCapture + paced send thread |
| `RaopClient.cs` | RTSP session, RTP/control/timing channels, AES/RSA, resend buffer |
| `AudioCapture.cs` | WASAPI loopback + resampling, device enumeration |
| `Discovery.cs` | mDNS scan + TXT parsing |
| `AlacEncoder.cs` | PCM → ALAC uncompressed frames |
| `AudioSwitch.cs` | default-output switching (IPolicyConfig) |

## License

[MIT](LICENSE)
