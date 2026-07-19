using System.Diagnostics;
using Microsoft.Win32;

namespace AirLift;

public sealed class TrayApp : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AirLift";
    private const string VbCableUrl = "https://vb-audio.com/Cable/";

    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly StreamEngine _engine = new();
    private readonly Settings _settings = Settings.Load();

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _speakersMenu;
    private readonly ToolStripMenuItem _sourceMenu;
    private readonly ToolStripMenuItem _volumeUpItem;
    private readonly ToolStripMenuItem _volumeDownItem;
    private readonly ToolStripMenuItem _muteItem;
    private readonly ToolStripMenuItem _latencyMenu;
    private readonly ToolStripMenuItem _latencyUltraItem;
    private readonly ToolStripMenuItem _latencyLowItem;
    private readonly ToolStripMenuItem _latencyStableItem;
    private readonly ToolStripMenuItem _autoReconnectItem;
    private readonly ToolStripMenuItem _disconnectItem;
    private readonly ToolStripMenuItem _autoConnectItem;
    private readonly ToolStripMenuItem _autoSwitchItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _vbCableItem;
    private readonly ToolStripMenuItem _languageMenu;
    private readonly ToolStripMenuItem _langKoItem;
    private readonly ToolStripMenuItem _langEnItem;
    private readonly ToolStripMenuItem _exitItem;

    private readonly Icon _iconIdle;
    private readonly Icon[] _iconLevels = new Icon[7]; // connected icons: level meter steps 0..6
    private readonly System.Windows.Forms.Timer _meterTimer;
    private int _lastIconStep = -1;

    private List<SpeakerInfo> _speakers = new();
    private bool _scanning;
    private bool _muted;
    private SynchronizationContext? _sync;
    private CancellationTokenSource? _reconnectCts;

    public TrayApp()
    {
        L.Lang = _settings.Language;

        _iconIdle = MakeIcon(Color.FromArgb(160, 160, 160), -1);
        for (int i = 0; i < _iconLevels.Length; i++)
            _iconLevels[i] = MakeIcon(Color.FromArgb(0, 160, 255), i);

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _speakersMenu = new ToolStripMenuItem();
        _sourceMenu = new ToolStripMenuItem();
        _volumeUpItem = new ToolStripMenuItem(string.Empty, null, async (_, _) => await ChangeVolumeAsync(+2));
        _volumeDownItem = new ToolStripMenuItem(string.Empty, null, async (_, _) => await ChangeVolumeAsync(-2));
        _muteItem = new ToolStripMenuItem(string.Empty, null, async (_, _) => await ToggleMuteAsync());
        _latencyUltraItem = new ToolStripMenuItem(string.Empty, null, async (_, _) => await SetLatencyModeAsync("Ultra"));
        _latencyLowItem = new ToolStripMenuItem(string.Empty, null, async (_, _) => await SetLatencyModeAsync("Low"));
        _latencyStableItem = new ToolStripMenuItem(string.Empty, null, async (_, _) => await SetLatencyModeAsync("Stable"));
        _latencyMenu = new ToolStripMenuItem();
        _latencyMenu.DropDownItems.AddRange(new ToolStripItem[] { _latencyUltraItem, _latencyLowItem, _latencyStableItem });
        _autoReconnectItem = new ToolStripMenuItem { CheckOnClick = true, Checked = _settings.AutoReconnect };
        _autoReconnectItem.CheckedChanged += (_, _) => { _settings.AutoReconnect = _autoReconnectItem.Checked; _settings.Save(); };
        _disconnectItem = new ToolStripMenuItem(string.Empty, null, async (_, _) => await DisconnectAsync(userRequested: true)) { Enabled = false };
        _autoConnectItem = new ToolStripMenuItem { CheckOnClick = true, Checked = _settings.AutoConnect };
        _autoConnectItem.CheckedChanged += (_, _) => { _settings.AutoConnect = _autoConnectItem.Checked; _settings.Save(); };
        _autoSwitchItem = new ToolStripMenuItem { CheckOnClick = true, Checked = _settings.AutoSwitchDefault };
        _autoSwitchItem.CheckedChanged += (_, _) => { _settings.AutoSwitchDefault = _autoSwitchItem.Checked; _settings.Save(); };
        _startupItem = new ToolStripMenuItem { CheckOnClick = true, Checked = IsStartupEnabled() };
        _startupItem.CheckedChanged += (_, _) => SetStartupEnabled(_startupItem.Checked);
        _vbCableItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowVbCableInfo());
        _langKoItem = new ToolStripMenuItem("한국어", null, (_, _) => SetLanguage("ko"));
        _langEnItem = new ToolStripMenuItem("English", null, (_, _) => SetLanguage("en"));
        _languageMenu = new ToolStripMenuItem();
        _languageMenu.DropDownItems.AddRange(new ToolStripItem[] { _langKoItem, _langEnItem });
        _exitItem = new ToolStripMenuItem(string.Empty, null, async (_, _) => await ExitAsync());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            _speakersMenu,
            _sourceMenu,
            new ToolStripSeparator(),
            _volumeUpItem,
            _volumeDownItem,
            _muteItem,
            _latencyMenu,
            _disconnectItem,
            new ToolStripSeparator(),
            _autoConnectItem,
            _autoReconnectItem,
            _autoSwitchItem,
            _startupItem,
            _vbCableItem,
            _languageMenu,
            new ToolStripSeparator(),
            _exitItem,
        });

        _speakersMenu.DropDownOpening += async (_, _) => await RefreshSpeakersAsync();
        _sourceMenu.DropDownOpening += (_, _) => RefreshSources();

        _trayIcon = new NotifyIcon
        {
            Icon = _iconIdle,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += async (_, _) => await ToggleConnectionAsync();

        ApplyTexts();
        _speakersMenu.DropDownItems.Add(new ToolStripMenuItem(L.Scanning) { Enabled = false });

        _engine.Error += msg => _sync?.Post(_ =>
        {
            UpdateStatus();
            Balloon(L.BalloonDropped, msg, ToolTipIcon.Warning);
            if (_settings.AutoReconnect && _settings.LastSpeakerName != null)
                StartReconnect(); // keep default output switched while retrying
            else
                RestoreDefaultOutput();
        }, null);

        _meterTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _meterTimer.Tick += (_, _) => UpdateMeterIcon();
        _meterTimer.Start();

        Application.Idle += OnFirstIdle;
    }

    private void UpdateMeterIcon()
    {
        int step;
        if (!_engine.IsConnected)
        {
            step = -1;
        }
        else
        {
            // Map level (peak 0..1) to 0..6 with a rough dB-ish curve
            float level = _engine.Level;
            step = level <= 0.001f ? 0 : 1 + (int)Math.Clamp(Math.Round((1 + Math.Log10(level) / 2.5) * 5), 0, 5);
        }
        if (step == _lastIconStep) return;
        _lastIconStep = step;
        _trayIcon.Icon = step < 0 ? _iconIdle : _iconLevels[step];
    }

    /// <summary>Applies the current language to all static menu items.</summary>
    private void ApplyTexts()
    {
        _speakersMenu.Text = L.Speakers;
        _sourceMenu.Text = L.InputSource;
        _volumeUpItem.Text = L.VolumeUp;
        _volumeDownItem.Text = L.VolumeDown;
        _muteItem.Text = L.Mute;
        _latencyMenu.Text = L.LatencyMenu;
        _latencyUltraItem.Text = L.LatencyUltra;
        _latencyLowItem.Text = L.LatencyLow;
        _latencyStableItem.Text = L.LatencyStable;
        _disconnectItem.Text = L.Disconnect;
        _autoConnectItem.Text = L.AutoConnect;
        _autoReconnectItem.Text = L.AutoReconnect;
        _autoSwitchItem.Text = L.AutoSwitch;
        _startupItem.Text = L.RunAtStartup;
        _vbCableItem.Text = L.VbCableMenu;
        _languageMenu.Text = L.LanguageMenu;
        _exitItem.Text = L.Exit;
        _langKoItem.Checked = L.Lang == "ko";
        _langEnItem.Checked = L.Lang == "en";
        UpdateLatencyChecks();
        UpdateStatus();
    }

    private void SetLanguage(string lang)
    {
        L.Lang = lang;
        _settings.Language = lang;
        _settings.Save();
        ApplyTexts();
        RebuildSpeakerMenu();
    }

    private async void OnFirstIdle(object? sender, EventArgs e)
    {
        Application.Idle -= OnFirstIdle;
        _sync = SynchronizationContext.Current;

        // Restore default output left switched by a crash, unless auto-connect will reuse it
        if (!_settings.AutoConnect)
            RestoreDefaultOutput();

        // Default the capture source to a virtual cable if installed and nothing saved
        if (_settings.CaptureDeviceId == null && AudioCapture.FindVirtualCable() is { } cable)
        {
            _settings.CaptureDeviceId = cable.Id;
            _settings.Save();
        }

        await RefreshSpeakersAsync();

        if (_settings.AutoConnect && _settings.LastSpeakerName != null)
        {
            var target = _speakers.FirstOrDefault(s => s.Name == _settings.LastSpeakerName);
            if (target != null)
                await ConnectAsync(target);
        }
    }

    // ---- speakers ----

    private async Task RefreshSpeakersAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            var found = await Discovery.ScanAsync(TimeSpan.FromSeconds(3));
            // mDNS is flaky within one window: merge, keep devices seen before
            foreach (var s in found)
                _speakers.RemoveAll(old => old.Name == s.Name);
            _speakers.AddRange(found);
            _speakers = _speakers.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            RebuildSpeakerMenu();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"scan failed: {ex}");
        }
        finally
        {
            _scanning = false;
        }
    }

    private void RebuildSpeakerMenu()
    {
        _speakersMenu.DropDownItems.Clear();
        if (_speakers.Count == 0)
        {
            _speakersMenu.DropDownItems.Add(new ToolStripMenuItem(L.NoSpeakersFound) { Enabled = false });
        }
        foreach (var speaker in _speakers)
        {
            var item = new ToolStripMenuItem(speaker.Name)
            {
                Checked = _engine.Speaker?.Name == speaker.Name,
                Enabled = !speaker.PasswordProtected,
            };
            if (speaker.PasswordProtected) item.Text += L.PasswordSuffix;
            var captured = speaker;
            item.Click += async (_, _) => await ConnectAsync(captured);
            _speakersMenu.DropDownItems.Add(item);
        }
        _speakersMenu.DropDownItems.Add(new ToolStripSeparator());
        var rescan = new ToolStripMenuItem(L.Rescan);
        rescan.Click += async (_, _) => await RefreshSpeakersAsync();
        _speakersMenu.DropDownItems.Add(rescan);
    }

    // ---- capture sources ----

    private void RefreshSources()
    {
        _sourceMenu.DropDownItems.Clear();

        var defaultItem = new ToolStripMenuItem(L.DefaultDevice)
        {
            Checked = _settings.CaptureDeviceId == null,
        };
        defaultItem.Click += async (_, _) => await SelectSourceAsync(null);
        _sourceMenu.DropDownItems.Add(defaultItem);

        foreach (var (id, name) in AudioCapture.GetRenderDevices())
        {
            var item = new ToolStripMenuItem(name) { Checked = _settings.CaptureDeviceId == id };
            var capturedId = id;
            item.Click += async (_, _) => await SelectSourceAsync(capturedId);
            _sourceMenu.DropDownItems.Add(item);
        }
    }

    private async Task SelectSourceAsync(string? deviceId)
    {
        _settings.CaptureDeviceId = deviceId;
        _settings.Save();
        // Restart the stream with the new source if currently connected
        if (_engine.Speaker is { } current)
            await ConnectAsync(current);
    }

    // ---- latency ----

    private void UpdateLatencyChecks()
    {
        _latencyUltraItem.Checked = _settings.LatencyMode == "Ultra";
        _latencyStableItem.Checked = _settings.LatencyMode == "Stable";
        _latencyLowItem.Checked = !_latencyUltraItem.Checked && !_latencyStableItem.Checked;
    }

    private async Task SetLatencyModeAsync(string mode)
    {
        _settings.LatencyMode = mode;
        _settings.Save();
        UpdateLatencyChecks();
        // Applies at session start - reconnect if streaming
        if (_engine.Speaker is { } current)
            await ConnectAsync(current);
    }

    // ---- connection ----

    private async Task ConnectAsync(SpeakerInfo speaker)
    {
        CancelReconnect();
        UpdateStatus(L.StatusConnecting(speaker.Name));
        try
        {
            _muted = false;
            await _engine.StartAsync(speaker, _settings.CaptureDeviceId, _settings.VolumeDb, _settings.LatencyMode);
            _settings.LastSpeakerName = speaker.Name;
            _settings.Save();
            SwitchDefaultOutput();
            UpdateStatus();
            Balloon(L.BalloonConnected, $"{speaker.Name}\n{L.InputLabel(_engine.CaptureDeviceName ?? "?")}", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            UpdateStatus();
            Balloon(L.BalloonConnectFailed, $"{speaker.Name}: {ex.Message}", ToolTipIcon.Error);
        }
    }

    // ---- auto-reconnect ----

    private void CancelReconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectCts = null;
    }

    private async void StartReconnect()
    {
        CancelReconnect();
        var cts = _reconnectCts = new CancellationTokenSource();
        int[] delaysSec = { 2, 5, 10, 15 };

        for (int attempt = 0; !cts.IsCancellationRequested; attempt++)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(delaysSec[Math.Min(attempt, delaysSec.Length - 1)]), cts.Token); }
            catch (OperationCanceledException) { return; }

            var name = _settings.LastSpeakerName;
            if (name == null) return;
            var target = _speakers.FirstOrDefault(s => s.Name == name);
            if (target == null)
            {
                await RefreshSpeakersAsync();
                target = _speakers.FirstOrDefault(s => s.Name == name);
                if (target == null) continue;
            }
            if (cts.IsCancellationRequested) return;

            UpdateStatus(L.Reconnecting(target.Name));
            try
            {
                _muted = false;
                await _engine.StartAsync(target, _settings.CaptureDeviceId, _settings.VolumeDb, _settings.LatencyMode);
                SwitchDefaultOutput();
                UpdateStatus();
                Balloon(L.BalloonReconnected, target.Name, ToolTipIcon.Info);
                if (_reconnectCts == cts) _reconnectCts = null;
                return;
            }
            catch
            {
                UpdateStatus(); // silent retry - balloon spam is worse than a quiet wait
            }
        }
    }

    /// <summary>Routes system audio to the capture device so sound goes to AirPlay only.</summary>
    private void SwitchDefaultOutput()
    {
        if (!_settings.AutoSwitchDefault || _settings.CaptureDeviceId == null) return;
        try
        {
            var currentDefault = AudioSwitch.GetDefaultRenderDeviceId();
            if (currentDefault == _settings.CaptureDeviceId) return;
            // Keep the original device across reconnects; only record it once
            _settings.PreviousDefaultDeviceId ??= currentDefault;
            _settings.Save();
            AudioSwitch.SetDefaultRenderDevice(_settings.CaptureDeviceId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"default switch failed: {ex.Message}");
        }
    }

    private void RestoreDefaultOutput()
    {
        var previous = _settings.PreviousDefaultDeviceId;
        if (previous == null) return;
        _settings.PreviousDefaultDeviceId = null;
        _settings.Save();
        try
        {
            if (AudioSwitch.DeviceExists(previous))
                AudioSwitch.SetDefaultRenderDevice(previous);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"default restore failed: {ex.Message}");
        }
    }

    private async Task DisconnectAsync(bool userRequested)
    {
        CancelReconnect();
        await _engine.StopAsync();
        RestoreDefaultOutput();
        UpdateStatus();
        if (userRequested)
            Balloon(L.BalloonDisconnected, L.StreamingStopped, ToolTipIcon.Info);
    }

    private async Task ToggleConnectionAsync()
    {
        if (_engine.IsConnected)
        {
            await DisconnectAsync(userRequested: true);
        }
        else if (_settings.LastSpeakerName != null)
        {
            var target = _speakers.FirstOrDefault(s => s.Name == _settings.LastSpeakerName);
            if (target == null)
            {
                await RefreshSpeakersAsync();
                target = _speakers.FirstOrDefault(s => s.Name == _settings.LastSpeakerName);
            }
            if (target != null)
                await ConnectAsync(target);
        }
    }

    // ---- volume ----

    private async Task ChangeVolumeAsync(double delta)
    {
        _settings.VolumeDb = Math.Clamp(_settings.VolumeDb + delta, -30, 0);
        _settings.Save();
        _muted = false;
        try { await _engine.SetVolumeAsync(_settings.VolumeDb); }
        catch (Exception ex) { Debug.WriteLine(L.VolumeChangeFailed(ex.Message)); }
        UpdateStatus();
    }

    private async Task ToggleMuteAsync()
    {
        _muted = !_muted;
        try { await _engine.SetVolumeAsync(_muted ? -144 : _settings.VolumeDb); }
        catch (Exception ex) { Debug.WriteLine(L.VolumeChangeFailed(ex.Message)); }
        UpdateStatus();
    }

    // ---- helpers ----

    private void UpdateStatus(string? overrideText = null)
    {
        string text;
        if (overrideText != null)
            text = overrideText;
        else if (_engine.Speaker is { } sp)
            text = L.StatusConnected(sp.Name, _settings.VolumeDb, _muted);
        else
            text = L.NotConnected;

        _statusItem.Text = text;
        _disconnectItem.Enabled = _engine.IsConnected;
        _muteItem.Checked = _muted;
        _lastIconStep = int.MinValue; // force meter icon refresh
        UpdateMeterIcon();
        var tip = $"AirLift - {text}";
        _trayIcon.Text = tip.Length <= 63 ? tip : tip[..63]; // NotifyIcon text limit

        foreach (var item in _speakersMenu.DropDownItems.OfType<ToolStripMenuItem>())
            item.Checked = _engine.Speaker != null && item.Text == _engine.Speaker.Name;
    }

    private void Balloon(string title, string text, ToolTipIcon icon)
        => _trayIcon.ShowBalloonTip(3000, $"AirLift - {title}", text, icon);

    private void ShowVbCableInfo()
    {
        var cable = AudioCapture.FindVirtualCable();
        bool installed = cable != null;
        var message = L.VbDialogBody(installed, cable?.Name);

        if (installed)
        {
            MessageBox.Show(message, L.VbDialogTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else if (MessageBox.Show(message, L.VbDialogTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                 == DialogResult.Yes)
        {
            Process.Start(new ProcessStartInfo(VbCableUrl) { UseShellExecute = true });
        }
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }

    private static void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
        else
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    /// <param name="meterStep">-1: no meter (idle). 0..6: level bar inside the display rectangle.</param>
    private static Icon MakeIcon(Color color, int meterStep)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Pen(color, 3);
            g.DrawRoundedRectangle(pen, new Rectangle(3, 6, 26, 15), new Size(4, 4));
            using var brush = new SolidBrush(color);
            g.FillPolygon(brush, new[] { new Point(16, 15), new Point(7, 29), new Point(25, 29) });
            if (meterStep > 0)
            {
                // Level bar inside the "display": grows left to right
                using var barBrush = new SolidBrush(Color.FromArgb(90, 230, 120));
                int width = 22 * meterStep / 6;
                g.FillRectangle(barBrush, 5, 9, width, 9);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private async Task ExitAsync()
    {
        CancelReconnect();
        _meterTimer.Stop();
        _trayIcon.Visible = false;
        await _engine.StopAsync();
        RestoreDefaultOutput();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _menu.Dispose();
            _engine.Dispose();
        }
        base.Dispose(disposing);
    }
}
