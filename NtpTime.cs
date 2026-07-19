namespace AirLift;

/// <summary>NTP timestamp helpers (seconds since 1900-01-01 + 32-bit fraction).</summary>
public static class NtpTime
{
    private static readonly DateTime Epoch1900 = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static ulong Now()
    {
        long ticks = (DateTime.UtcNow - Epoch1900).Ticks;
        ulong seconds = (ulong)(ticks / TimeSpan.TicksPerSecond);
        ulong fracTicks = (ulong)(ticks % TimeSpan.TicksPerSecond);
        ulong fraction = (fracTicks << 32) / (ulong)TimeSpan.TicksPerSecond;
        return (seconds << 32) | fraction;
    }

    public static void WriteBigEndian(byte[] buf, int offset, ulong value)
    {
        for (int i = 0; i < 8; i++)
            buf[offset + i] = (byte)(value >> (56 - i * 8));
    }
}
