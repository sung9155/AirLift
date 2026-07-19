using NAudio.CoreAudioApi;

namespace AirOutput;

/// <summary>Hidden CLI diagnostics: run with --dump-devices or --peak-test (output via redirected stdout).</summary>
public static class Diagnostics
{
    public static void DumpDevices()
    {
        var settings = Settings.Load();
        using var enumerator = new MMDeviceEnumerator();
        var defaultDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        Console.WriteLine($"default render device: {defaultDev.FriendlyName}");
        Console.WriteLine($"settings.CaptureDeviceId: {settings.CaptureDeviceId ?? "(null = default)"}");
        Console.WriteLine($"settings.LastSpeakerName: {settings.LastSpeakerName}");
        Console.WriteLine($"settings.VolumeDb: {settings.VolumeDb}");
        Console.WriteLine();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var marks = "";
            if (d.ID == defaultDev.ID) marks += " [DEFAULT]";
            if (d.ID == settings.CaptureDeviceId) marks += " [CAPTURE TARGET]";
            Console.WriteLine($"{d.FriendlyName}{marks}");
            Console.WriteLine($"    id: {d.ID}");
            Console.WriteLine($"    format: {d.AudioClient.MixFormat}");
        }
    }

    /// <summary>Captures the configured device for N seconds, prints peak level per second.</summary>
    public static void PeakTest(int seconds)
    {
        var settings = Settings.Load();
        using var capture = new AudioCapture(settings.CaptureDeviceId);
        Console.WriteLine($"capturing: {capture.DeviceName} for {seconds}s...");
        capture.Start();

        var buf = new byte[44100 * 4]; // 1 second of 16-bit stereo
        for (int s = 0; s < seconds; s++)
        {
            Thread.Sleep(1000);
            capture.Read(buf, buf.Length);
            short peak = 0;
            for (int i = 0; i < buf.Length; i += 2)
            {
                short v = (short)(buf[i] | (buf[i + 1] << 8));
                if (v == short.MinValue) v = short.MaxValue;
                if (Math.Abs((int)v) > peak) peak = Math.Abs((int)v) > short.MaxValue ? short.MaxValue : (short)Math.Abs((int)v);
            }
            Console.WriteLine($"  t={s + 1}s peak={peak} ({(peak == 0 ? "SILENT" : $"{20 * Math.Log10(peak / 32768.0):0.0} dBFS")})");
        }
    }
}
