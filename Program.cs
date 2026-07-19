namespace AirLift;

internal sealed class DualWriter(TextWriter a, TextWriter b) : TextWriter
{
    public override System.Text.Encoding Encoding => a.Encoding;
    public override void Write(char value) { a.Write(value); b.Write(value); }
    public override void WriteLine(string? value) { a.WriteLine(value); b.WriteLine(value); }
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--dump-devices") || args.Contains("--peak-test"))
        {
            // GUI subsystem: stdout capture is unreliable, write to a log file too
            var log = new StringWriter();
            var dual = new DualWriter(Console.Out, log);
            Console.SetOut(dual);
            try
            {
                if (args.Contains("--dump-devices")) Diagnostics.DumpDevices();
                else
                {
                    int idx = Array.IndexOf(args, "--peak-test");
                    int seconds = idx + 1 < args.Length && int.TryParse(args[idx + 1], out var s) ? s : 5;
                    Diagnostics.PeakTest(seconds);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DIAG ERROR: {ex}");
            }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "airlift-diag.log"), log.ToString());
            return;
        }

        using var mutex = new Mutex(true, "AirLift_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            L.Lang = Settings.Load().Language;
            MessageBox.Show(L.AlreadyRunning, "AirLift", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.Run(new TrayApp());
    }
}
