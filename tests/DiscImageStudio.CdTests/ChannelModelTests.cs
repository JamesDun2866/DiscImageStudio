using DiscImageStudio.Cd;
using DiscImageStudio.Cd.Physical;

/// <summary>
/// Checks the reference CD channel model against the standard it implements (ECMA-130 Annexes
/// C–E) and against the generator's own delay-interleave compensation, which real burns validate.
/// </summary>
internal static class ChannelModelTests
{
    internal static void Run()
    {
        TestEfmTable();
        TestParityEquations();
        TestCircDelaysMatchFigureC4();
        TestGeneratorInterleaveRoundTrip();
        TestSteadyStateMatchesFullChain();
        TestChannelConstraints();
        TestReferenceFigures();
        Console.WriteLine("channel model: passed");
    }

    /// <summary>Annex D: 256 distinct words obeying the run-length rule; spot values from Table D.1.</summary>
    private static void TestEfmTable()
    {
        ReadOnlySpan<ushort> words = CdEfmTable.Words;
        Check.Equal(256, words.Length, "EFM table size");
        Check.Equal(256, new HashSet<ushort>(words.ToArray()).Count, "EFM words are distinct");
        foreach (ushort word in words)
        {
            Check.True(word is > 0 and < 1 << 14, $"EFM word {word:X4} has 14 bits and a ONE");
            int previous = -1;
            for (int bit = 13; bit >= 0; bit--)
            {
                if (((word >> bit) & 1) == 0) continue;
                if (previous >= 0)
                {
                    int zeros = previous - bit - 1;
                    Check.True(zeros is >= 2 and <= 10, $"EFM word {word:X4} run length");
                }

                previous = bit;
            }
        }

        Check.Equal((ushort)0b01001000100000, CdEfmTable.Word(0x00), "EFM 00");
        Check.Equal((ushort)0b00000001000000, CdEfmTable.Word(0x0D), "EFM 0D");
        Check.Equal((ushort)0b10000000100000, CdEfmTable.Word(0x10), "EFM 10");
        Check.Equal((ushort)0b10001000100010, CdEfmTable.Word(0x63), "EFM 63");
        Check.Equal((ushort)0b10010001001001, CdEfmTable.Word(0xAA), "EFM AA");
        Check.Equal((ushort)0b00100000010010, CdEfmTable.Word(0xFF), "EFM FF");
        Check.True(!words.Contains(CdEfmTable.Sync0) && !words.Contains(CdEfmTable.Sync1),
            "subcode sync patterns are not data words");
    }

    /// <summary>Annex C.7: H·V = 0 for both codes; all-zero data gives all-FF inverted parity.</summary>
    private static void TestParityEquations()
    {
        Random random = new(130);
        Span<byte> syndromes = stackalloc byte[4];
        byte[] c2 = new byte[28];
        byte[] c1 = new byte[32];
        for (int trial = 0; trial < 500; trial++)
        {
            random.NextBytes(c2);
            CdCircParity.EncodeC2(c2);
            CdCircParity.Syndromes(c2, syndromes);
            Check.True(syndromes.IndexOfAnyExcept((byte)0) < 0, "C2 syndromes vanish");

            random.NextBytes(c1);
            CdCircParity.EncodeC1(c1);
            CdCircParity.Syndromes(c1, syndromes);
            Check.True(syndromes.IndexOfAnyExcept((byte)0) < 0, "C1 syndromes vanish");
        }

        // A single wrong byte is detected.
        c1[5] ^= 0x40;
        CdCircParity.Syndromes(c1, syndromes);
        Check.True(syndromes.IndexOfAnyExcept((byte)0) >= 0, "C1 detects a corrupted byte");

        CdCircEncoder encoder = new();
        byte[] f2 = new byte[32];
        encoder.Encode(new byte[24], f2);
        for (int slot = 0; slot < 32; slot++)
        {
            Check.Equal(CdCircEncoder.IsDataSlot(slot) ? (byte)0 : (byte)0xFF, f2[slot], $"silence slot {slot}");
        }
    }

    /// <summary>
    /// Figure C.4 lists, for every F2 slot, which input byte it carries and how many frames later.
    /// An impulse in each input byte must surface in exactly that data slot after that delay; this
    /// checks the encoder's delay sections against the standard's independent output table.
    /// </summary>
    private static void TestCircDelaysMatchFigureC4()
    {
        // slot -> (word, B byte?, delay in frames), D = 4.
        (int Slot, int Word, bool B, int Delay)[] table =
        [
            (0, 0, false, 3), (1, 0, true, 6), (2, 4, false, 11), (3, 4, true, 14),
            (4, 8, false, 19), (5, 8, true, 22), (6, 1, false, 27), (7, 1, true, 30),
            (8, 5, false, 35), (9, 5, true, 38), (10, 9, false, 43), (11, 9, true, 46),
            (16, 2, false, 65), (17, 2, true, 68), (18, 6, false, 73), (19, 6, true, 76),
            (20, 10, false, 81), (21, 10, true, 84), (22, 3, false, 89), (23, 3, true, 92),
            (24, 7, false, 97), (25, 7, true, 100), (26, 11, false, 105), (27, 11, true, 108),
        ];
        const int inputFrame = 5;
        foreach ((int slot, int word, bool b, int delay) in table)
        {
            int inputByte = (2 * word) + (b ? 1 : 0);
            CdCircEncoder encoder = new();
            byte[] f1 = new byte[24];
            byte[] f2 = new byte[32];
            List<(int Frame, int Slot)> hits = [];
            for (int frame = 0; frame < inputFrame + 120; frame++)
            {
                Array.Clear(f1);
                if (frame == inputFrame) f1[inputByte] = 0x5A;
                encoder.Encode(f1, f2);
                for (int s = 0; s < 32; s++)
                {
                    if (CdCircEncoder.IsDataSlot(s) && f2[s] != 0) hits.Add((frame, s));
                }
            }

            Check.Equal(1, hits.Count, $"impulse in byte {inputByte} reaches one data slot");
            Check.Equal(slot, hits[0].Slot, $"byte {inputByte} slot");
            Check.Equal(inputFrame + delay, hits[0].Frame, $"byte {inputByte} delay");
        }
    }

    /// <summary>
    /// The generator's compensation must undo the CIRC delays: after interleaving, CIRC encoding
    /// puts logical byte i of logical frame t into data slot i (i &lt; 12) or i + 4, all at one
    /// constant frame offset. Without the interleave the order is scrambled (negative control).
    /// </summary>
    private static void TestGeneratorInterleaveRoundTrip()
    {
        const int sectors = 12;
        byte[] logical = new byte[sectors * CdDiscParameters.BytesPerSector];
        for (int index = 0; index < logical.Length; index++)
        {
            logical[index] = (byte)((index * 151) ^ (index >> 8) ^ (index >> 13));
        }

        int? offset = FindSlotOffset(CdChannelModel.InterleaveLogical(logical), logical);
        Check.True(offset.HasValue, "interleaved logical frames appear in order in the data slots");
        Check.True(FindSlotOffset(logical, logical) is null, "without the interleave the data slots are scrambled");
    }

    private static int? FindSlotOffset(byte[] f1Bytes, byte[] logical)
    {
        List<byte[]> frames = [];
        CdChannelModel.EncodeF1Stream(f1Bytes, (_, f2) => frames.Add(f2.ToArray()));
        int logicalFrames = logical.Length / 24;
        for (int offset = 0; offset < 200; offset++)
        {
            bool all = true;
            int compared = 0;
            for (int t = 120; t < logicalFrames - 200 && all; t++)
            {
                int physical = t + offset;
                if (physical >= frames.Count) break;
                for (int i = 0; i < 24; i++)
                {
                    int slot = i < 12 ? i : i + 4;
                    if (frames[physical][slot] != logical[(t * 24) + i])
                    {
                        all = false;
                        break;
                    }
                }

                compared++;
            }

            if (all && compared > 500) return offset;
        }

        return null;
    }

    /// <summary>A long uniform region settles into the steady-state frame, parity included.</summary>
    private static void TestSteadyStateMatchesFullChain()
    {
        foreach (string text in new[] { "0D", "AA", "63", "F2-44", "15-AA" })
        {
            CdBytePattern pattern = CdBytePattern.Parse(text);
            byte[] logical = new byte[8 * CdDiscParameters.BytesPerSector];
            for (int index = 0; index < logical.Length; index++) logical[index] = pattern.At(index);
            byte[] expected = CdChannelModel.SteadyStateFrame(pattern);
            int compared = 0;
            CdChannelModel.EncodeF1Stream(CdChannelModel.InterleaveLogical(logical), (frame, f2) =>
            {
                if (frame < 400 || frame > 600) return;
                Check.True(f2.SequenceEqual(expected), $"steady state of {text} at frame {frame}");
                compared++;
            });
            Check.True(compared > 100, $"steady state of {text} compared");

            CdChannelStatistics full = CdChannelModel.AnalyzePattern(pattern, new CdChannelOptions(Sectors: 12));
            CdChannelStatistics fast = CdChannelModel.AnalyzeSteadyState(pattern, new CdChannelOptions(Sectors: 12));
            Check.True(Math.Abs(full.TransitionsPerBit - fast.TransitionsPerBit) < 0.002,
                $"steady-state texture of {text} ({full.TransitionsPerBit:F4} vs {fast.TransitionsPerBit:F4})");
        }
    }

    /// <summary>Annex E constraints hold for random data and for every uniform byte.</summary>
    private static void TestChannelConstraints()
    {
        foreach (CdMergingTieBreak tieBreak in Enum.GetValues<CdMergingTieBreak>())
        {
            byte[] random = new byte[20 * CdDiscParameters.BytesPerSector];
            new Random(42).NextBytes(random);
            CdChannelStatistics statistics = CdChannelModel.AnalyzeLogical(random, new CdChannelOptions(TieBreak: tieBreak));
            Check.Equal(0L, statistics.RunLengthViolations, $"random data run lengths ({tieBreak})");
            Check.Equal(0L, statistics.FalseSyncs, $"random data false syncs ({tieBreak})");
            Check.Equal(statistics.Frames * CdEfmChannel.FrameBits, statistics.Bits, "588 channel bits per frame");
            Check.True(statistics.MaxAbsDsv < 100, $"random data DSV stays bounded ({statistics.MaxAbsDsv})");
            long histogram = 0;
            for (int length = 3; length <= 11; length++) histogram += statistics.RunHistogram[length];
            Check.True(histogram > 0 && statistics.RunHistogram[1] == 0 && statistics.RunHistogram[2] == 0,
                "only 3T..11T runs");

            for (int value = 0; value < 256; value++)
            {
                CdChannelStatistics uniform = CdChannelModel.AnalyzeSteadyState(
                    CdBytePattern.Single((byte)value), new CdChannelOptions(Sectors: 6, WarmupFrames: 98, TieBreak: tieBreak));
                Check.Equal(0L, uniform.RunLengthViolations, $"uniform {value:X2} run lengths ({tieBreak})");
                Check.Equal(0L, uniform.FalseSyncs, $"uniform {value:X2} false syncs ({tieBreak})");
            }
        }
    }

    /// <summary>
    /// Sanity against the upstream audit (docs/PHYSICAL_MODEL_AUDIT.md), which used an independent
    /// Python chain: pure 0D/AA stay within a small DSV, pure 63 carries more low-frequency power
    /// than random data, and pure 0D less. Exact percentages depend on estimator details, so only
    /// the ordering and magnitudes are checked.
    /// </summary>
    private static void TestReferenceFigures()
    {
        CdChannelOptions options = new(Sectors: 40, Spectrum: true);
        CdChannelStatistics dark = CdChannelModel.AnalyzePattern(CdBytePattern.Single(0x0D), options);
        CdChannelStatistics light = CdChannelModel.AnalyzePattern(CdBytePattern.Single(0xAA), options);
        CdChannelStatistics sixtyThree = CdChannelModel.AnalyzePattern(CdBytePattern.Single(0x63), options);
        byte[] randomBytes = new byte[40 * CdDiscParameters.BytesPerSector];
        new Random(7).NextBytes(randomBytes);
        CdChannelStatistics random = CdChannelModel.AnalyzeLogical(randomBytes, options);

        Check.True(dark.MaxAbsDsv <= 20 && light.MaxAbsDsv <= 20,
            $"pure 0D/AA stay near DSV 0 ({dark.MaxAbsDsv}/{light.MaxAbsDsv})");
        // The audit's independent Python chain reported max |DSV| 11 / 11 / 23 and low-frequency
        // shares 0.0476 % / 0.0445 % / 0.5561 % / 0.1696 % for pure 0D / AA / 63 / random data.
        Check.Equal(11L, dark.MaxAbsDsv, "audit: pure 0D max |DSV|");
        Check.Equal(11L, light.MaxAbsDsv, "audit: pure AA max |DSV|");
        Check.Equal(23L, sixtyThree.MaxAbsDsv, "audit: pure 63 max |DSV|");
        Check.True(Math.Abs(dark.LowFrequencyFraction - 0.000476) < 0.00005, "audit: pure 0D low-frequency share");
        Check.True(Math.Abs(light.LowFrequencyFraction - 0.000445) < 0.00005, "audit: pure AA low-frequency share");
        Check.True(Math.Abs(sixtyThree.LowFrequencyFraction - 0.005561) < 0.0003, "audit: pure 63 low-frequency share");
        Check.True(Math.Abs(random.LowFrequencyFraction - 0.001696) < 0.0002, "audit: random low-frequency share");
        Check.True(sixtyThree.LowFrequencyFraction > random.LowFrequencyFraction,
            $"63 has more low-frequency power than random ({sixtyThree.LowFrequencyFraction:P3} vs {random.LowFrequencyFraction:P3})");
        Check.True(dark.LowFrequencyFraction < random.LowFrequencyFraction,
            $"0D has less low-frequency power than random ({dark.LowFrequencyFraction:P3} vs {random.LowFrequencyFraction:P3})");
        // EFM words alone give 8.5T vs 3.4T; the uncontrolled parity and subcode symbols pull both
        // toward the middle, which is why choosing candidates on the full stream matters.
        Check.True(dark.MeanRunT > light.MeanRunT * 1.5, $"0D is the long-run texture ({dark.MeanRunT:F2}T vs {light.MeanRunT:F2}T)");
        Console.WriteLine(
            $"  reference: 0D {dark.MeanRunT:F2}T dsv {dark.MaxAbsDsv} lf {dark.LowFrequencyFraction:P3}; "
            + $"AA {light.MeanRunT:F2}T dsv {light.MaxAbsDsv} lf {light.LowFrequencyFraction:P3}; "
            + $"63 dsv {sixtyThree.MaxAbsDsv} lf {sixtyThree.LowFrequencyFraction:P3}; "
            + $"random dsv {random.MaxAbsDsv} lf {random.LowFrequencyFraction:P3}");
    }
}
