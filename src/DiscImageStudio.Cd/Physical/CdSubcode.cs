namespace DiscImageStudio.Cd.Physical;

/// <summary>
/// Subcode bytes of an audio track for the channel model (ECMA-130 clause 22, IEC 908): each
/// 98-frame section starts with SYNC 0 / SYNC 1; the other 96 frames carry one bit each of the
/// P–W channels. P is 0 inside a track; Q carries mode-1 (ADR 1) position packets for track 1,
/// index 1, with the CRC-16 (x¹⁶+x¹²+x⁵+1) stored inverted; R–W are 0. Absolute time starts at
/// 00:02:00 like a first track after its 2-second pause. Recorders may lay this out slightly
/// differently (copy bit, section phase); the subcode is one symbol in 33, so the channel
/// statistics do not depend on such details.
/// </summary>
internal static class CdSubcode
{
    internal const int FramesPerSection = 98;
    private const int PregapSections = 150;

    /// <summary>The 14-bit channel word of the subcode symbol of frame <paramref name="frame"/>.</summary>
    internal static ushort ChannelWord(long frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        int inSection = (int)(frame % FramesPerSection);
        if (inSection == 0)
        {
            return CdEfmTable.Sync0;
        }

        if (inSection == 1)
        {
            return CdEfmTable.Sync1;
        }

        return CdEfmTable.Word(Byte(frame / FramesPerSection, inSection));
    }

    /// <summary>The P–W byte (P = bit 7 … W = bit 0) of frame 2..97 of a section.</summary>
    internal static byte Byte(long section, int frameInSection)
    {
        if (frameInSection is < 2 or >= FramesPerSection)
            throw new ArgumentOutOfRangeException(nameof(frameInSection));
        Span<byte> q = stackalloc byte[12];
        QPacket(section, q);
        int bit = frameInSection - 2;
        bool qBit = (q[bit >> 3] & (0x80 >> (bit & 7))) != 0;
        return qBit ? (byte)0x40 : (byte)0;
    }

    internal static void QPacket(long section, Span<byte> packet)
    {
        if (packet.Length < 12) throw new ArgumentException("Q packets have 12 bytes.", nameof(packet));
        packet[0] = 0x01; // CONTROL 0 (2-channel audio, no pre-emphasis), ADR 1
        packet[1] = 0x01; // track 01
        packet[2] = 0x01; // index 01
        WriteTime(section, packet[3..6]);
        packet[6] = 0;
        WriteTime(section + PregapSections, packet[7..10]);
        ushort crc = (ushort)~Crc16(packet[..10]);
        packet[10] = (byte)(crc >> 8);
        packet[11] = (byte)crc;
    }

    private static void WriteTime(long sections, Span<byte> time)
    {
        long minutes = sections / (60 * 75);
        long seconds = sections / 75 % 60;
        long frames = sections % 75;
        time[0] = Bcd(minutes % 100);
        time[1] = Bcd(seconds);
        time[2] = Bcd(frames);
    }

    private static byte Bcd(long value) => (byte)(((value / 10) << 4) | (value % 10));

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte value in data)
        {
            crc ^= value << 8;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
            }
        }

        return (ushort)crc;
    }
}
