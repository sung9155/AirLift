namespace AirLift;

/// <summary>
/// Wraps raw 16-bit stereo PCM into Apple Lossless (ALAC) frames using the
/// "uncompressed" escape mode, as used by RAOP senders (raop_play, node_airtunes).
/// </summary>
public static class AlacEncoder
{
    public const int FramesPerPacket = 352;
    public const int BytesPerFrame = 4; // 16-bit stereo

    /// <summary>
    /// Encodes one packet of PCM (little-endian 16-bit stereo, 352 frames = 1408 bytes)
    /// into an ALAC frame. Returns the encoded bytes.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> pcm)
    {
        int frames = pcm.Length / BytesPerFrame;
        // 23 header bits + 32 bits per stereo frame + 3-bit END tag, rounded up
        var output = new byte[(23 + frames * 32 + 3 + 7) / 8];
        int bitPos = 0;

        WriteBits(output, ref bitPos, 1, 3);  // channels: 1 = stereo
        WriteBits(output, ref bitPos, 0, 4);  // unknown
        WriteBits(output, ref bitPos, 0, 8);  // unknown
        WriteBits(output, ref bitPos, 0, 4);  // unknown
        WriteBits(output, ref bitPos, 0, 1);  // has-size flag
        WriteBits(output, ref bitPos, 0, 2);  // unused
        WriteBits(output, ref bitPos, 1, 1);  // is-not-compressed

        // Samples follow as big-endian 16-bit, interleaved L/R
        for (int i = 0; i < frames * 2; i++)
        {
            int sample = (ushort)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)); // LE -> value
            WriteBits(output, ref bitPos, (sample >> 8) & 0xFF, 8);
            WriteBits(output, ref bitPos, sample & 0xFF, 8);
        }

        // END element tag - required by ffmpeg's ALAC decoder (shairport-sync 5.x)
        WriteBits(output, ref bitPos, 7, 3);

        return output;
    }

    private static void WriteBits(byte[] buf, ref int bitPos, int value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            int bit = (value >> i) & 1;
            if (bit != 0)
                buf[bitPos >> 3] |= (byte)(0x80 >> (bitPos & 7));
            bitPos++;
        }
    }
}
