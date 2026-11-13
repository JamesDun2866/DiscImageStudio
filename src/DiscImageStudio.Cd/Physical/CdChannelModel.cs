using System.IO;

namespace DiscImageStudio.Cd.Physical;

/// <summary>How much track the channel model simulates and how it measures it.</summary>
internal sealed record CdChannelOptions(
    int Sectors = 40,
    int WarmupFrames = CdChannelModel.DefaultWarmupFrames,
    CdMergingTieBreak TieBreak = CdMergingTieBreak.PreferTransition,
    bool Spectrum = false,
    double VelocityMmPerSecond = 1200.0);

/// <summary>
/// Reference model of what a recorder writes for a CD-DA track: the generator's logical bytes go
/// through the generator's own delay-interleave compensation (<see cref="CddaInterleaver"/>,
/// big-endian, which is the F1-frame byte order), then the standard CIRC encoder, subcode and the
/// EFM channel coder, and the channel bits are measured. This is a standard-conforming reference,
/// not a model of a particular drive: firmware may break merging-bit ties differently, so callers
/// that judge safety evaluate both tie-break orders.
/// </summary>
internal static class CdChannelModel
{
    /// <summary>Frames discarded before measuring (four 98-frame sections, beyond the 109-frame
    /// CIRC span and the generator's interleave start-up).</summary>
    internal const int DefaultWarmupFrames = 392;

    /// <summary>Runs logical CD-DA bytes (whole 2352-byte sectors) through the full chain.</summary>
    internal static CdChannelStatistics AnalyzeLogical(ReadOnlySpan<byte> logical, CdChannelOptions? options = null)
    {
        options ??= new CdChannelOptions();
        CdChannelStatistics statistics = new(options.WarmupFrames, options.VelocityMmPerSecond, options.Spectrum);
        CdEfmChannel channel = new(options.TieBreak, statistics);
        EncodeF1Stream(InterleaveLogical(logical), (frame, f2) => channel.WriteFrame(CdSubcode.ChannelWord(frame), f2));
        return statistics;
    }

    /// <summary>Simulates a track filled with one byte pattern through the full chain.</summary>
    internal static CdChannelStatistics AnalyzePattern(CdBytePattern pattern, CdChannelOptions? options = null)
    {
        options ??= new CdChannelOptions();
        byte[] logical = new byte[checked(options.Sectors * CdDiscParameters.BytesPerSector)];
        for (int index = 0; index < logical.Length; index++)
        {
            logical[index] = pattern.At(index);
        }

        return AnalyzeLogical(logical, options);
    }

    /// <summary>
    /// Fast path for a uniform pattern: in the steady state every F2-frame is identical, so it is
    /// built directly (data slots alternate Even/Odd, Q and P from the codes, both inverted) and only
    /// the EFM channel is simulated. <see cref="AnalyzePattern"/> must agree (tested).
    /// </summary>
    internal static CdChannelStatistics AnalyzeSteadyState(CdBytePattern pattern, CdChannelOptions? options = null)
    {
        options ??= new CdChannelOptions();
        byte[] f2 = SteadyStateFrame(pattern);
        CdChannelStatistics statistics = new(options.WarmupFrames, options.VelocityMmPerSecond, options.Spectrum);
        CdEfmChannel channel = new(options.TieBreak, statistics);
        long frames = (long)options.Sectors * CdSubcode.FramesPerSection;
        for (long frame = 0; frame < frames; frame++)
        {
            channel.WriteFrame(CdSubcode.ChannelWord(frame), f2);
        }

        return statistics;
    }

    /// <summary>The F2-frame a long uniform region of <paramref name="pattern"/> settles into.</summary>
    internal static byte[] SteadyStateFrame(CdBytePattern pattern)
    {
        byte[] c2 = new byte[CdCircParity.C2Length];
        for (int position = 0; position < c2.Length; position++)
        {
            c2[position] = pattern.At(position);
        }

        CdCircParity.EncodeC2(c2);
        byte[] c1 = new byte[CdCircParity.C1Length];
        c2.CopyTo(c1, 0);
        CdCircParity.EncodeC1(c1);
        for (int slot = 0; slot < c1.Length; slot++)
        {
            if (!CdCircEncoder.IsDataSlot(slot)) c1[slot] = (byte)~c1[slot];
        }

        return c1;
    }

    /// <summary>The generator's delay-interleave compensation, in F1-frame byte order.</summary>
    internal static byte[] InterleaveLogical(ReadOnlySpan<byte> logical)
    {
        if (logical.Length % CdDiscParameters.BytesPerSector != 0)
            throw new ArgumentException("CD-DA input must be whole 2352-byte sectors.", nameof(logical));
        using MemoryStream output = new(logical.Length + CdDiscParameters.BytesPerSector);
        CddaInterleaver interleaver = new(CdAudioByteOrder.BigEndian);
        interleaver.Add(logical, output);
        interleaver.Flush(output);
        return output.ToArray();
    }

    /// <summary>Encodes F1-order bytes (whole 24-byte frames) and hands out each F2-frame.</summary>
    internal static void EncodeF1Stream(ReadOnlySpan<byte> f1Bytes, Action<long, ReadOnlySpan<byte>> onF2Frame)
    {
        CdCircEncoder encoder = new();
        Span<byte> f2 = stackalloc byte[CdCircEncoder.F2Bytes];
        int frames = f1Bytes.Length / CdCircEncoder.F1Bytes;
        for (int frame = 0; frame < frames; frame++)
        {
            encoder.Encode(f1Bytes.Slice(frame * CdCircEncoder.F1Bytes, CdCircEncoder.F1Bytes), f2);
            onF2Frame(frame, f2);
        }
    }
}
