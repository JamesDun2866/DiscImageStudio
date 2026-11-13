using DiscImageStudio.Core;

namespace DiscImageStudio.Cd.Physical;

/// <summary>Worst readability-related figures of one texture under both merging tie-breaks.</summary>
internal sealed record CdSafetyFigures(long MaxAbsDsv, double MaxBias216, double LowFrequencyProxy,
    long RunLengthViolations, long FalseSyncs)
{
    internal static CdSafetyFigures Of(CdChannelStatistics statistics)
        => new(statistics.MaxAbsDsv, statistics.MaxBias216, statistics.LowFrequencyProxy,
            statistics.RunLengthViolations, statistics.FalseSyncs);

    internal CdSafetyFigures Max(CdSafetyFigures other)
        => new(Math.Max(MaxAbsDsv, other.MaxAbsDsv), Math.Max(MaxBias216, other.MaxBias216),
            Math.Max(LowFrequencyProxy, other.LowFrequencyProxy),
            Math.Max(RunLengthViolations, other.RunLengthViolations), Math.Max(FalseSyncs, other.FalseSyncs));

    internal bool Within(CdSafetyFigures limit)
        => RunLengthViolations == 0 && FalseSyncs == 0
            && MaxAbsDsv <= limit.MaxAbsDsv
            && MaxBias216 <= limit.MaxBias216 + 1e-12
            && LowFrequencyProxy <= limit.LowFrequencyProxy + 1e-12;

    public override string ToString()
        => $"max|DSV| {MaxAbsDsv}, bias216 {MaxBias216:F3}, LF {LowFrequencyProxy:P3}"
            + (RunLengthViolations + FalseSyncs > 0 ? $", violations {RunLengthViolations}, false syncs {FalseSyncs}" : string.Empty);
}

internal sealed record CdPairAssessment(
    CdBytePattern Dark,
    CdBytePattern Light,
    bool Safe,
    CdSafetyFigures Worst,
    CdSafetyFigures Envelope,
    string? FailingScenario);

/// <summary>
/// Readability guard for custom tone bytes. Patterned data can defeat the recorder's DC control
/// (ECMA-130 Annex B/E; upstream issue #90), so a dark/light pair is accepted only if none of the
/// textures it can produce — pure fills, dither mixes and stripes — is worse than the textures the
/// shipped palette already produces (pure 0x10 / 0x63, the 4-level dither mix, the 0x0D/0xAA set),
/// under the same estimator and the worse of both merging tie-break orders. This keeps custom
/// bytes inside the envelope real burns have already exercised; it is not a readability proof.
/// </summary>
internal static class CdPatternSafety
{
    internal const int DefaultSectors = 16;
    private static readonly double[] MixFractions = [0.25, 0.5, 0.75];
    private static readonly int[] StripeWidths = [2, 4, 8, 12, 16, 24, 32, 48, 64, 96];
    private static readonly Lazy<CdSafetyFigures> ShippedEnvelope = new(BuildEnvelope);

    internal static readonly CdBytePattern StandardDark = CdBytePattern.Single(0x0D);
    internal static readonly CdBytePattern StandardLight = CdBytePattern.Single(0xAA);

    internal static CdSafetyFigures Envelope => ShippedEnvelope.Value;

    internal static CdPairAssessment AssessPair(CdBytePattern dark, CdBytePattern light, int sectors = DefaultSectors)
    {
        CdSafetyFigures envelope = Envelope;
        CdSafetyFigures? worst = null;
        string? failing = null;
        foreach ((string name, byte[] logical) in PairScenarios(dark, light, sectors))
        {
            CdSafetyFigures figures = Measure(logical);
            worst = worst is null ? figures : worst.Max(figures);
            if (failing is null && !figures.Within(envelope)) failing = $"{name}: {figures}";
        }

        return new CdPairAssessment(dark, light, failing is null, worst!, envelope, failing);
    }

    /// <summary>Figures of one logical byte stream: worse of the two tie-break orders.</summary>
    internal static CdSafetyFigures Measure(byte[] logical)
    {
        CdSafetyFigures[] results = new CdSafetyFigures[2];
        Parallel.For(0, 2, index =>
        {
            CdMergingTieBreak tieBreak = index == 0 ? CdMergingTieBreak.PreferTransition : CdMergingTieBreak.PreferNone;
            results[index] = CdSafetyFigures.Of(CdChannelModel.AnalyzeLogical(logical,
                new CdChannelOptions(TieBreak: tieBreak)));
        });
        return results[0].Max(results[1]);
    }

    internal static IEnumerable<(string Name, byte[] Logical)> PairScenarios(CdBytePattern dark, CdBytePattern light, int sectors)
    {
        int length = checked(sectors * CdDiscParameters.BytesPerSector);
        bool perPair = !dark.IsSingle || !light.IsSingle;
        yield return ("pure dark", Fill(length, _ => false, dark, light));
        yield return ("pure light", Fill(length, _ => true, dark, light));
        foreach (double fraction in MixFractions)
        {
            yield return ($"{fraction:P0} light dither", Fill(length,
                index => ToneDither.Phase(perPair ? index >> 1 : index) < fraction, dark, light));
        }

        foreach (int width in StripeWidths)
        {
            yield return ($"{width}-byte stripes", Fill(length, index => index / width % 2 == 1, dark, light));
        }
    }

    private static byte[] Fill(int length, Func<long, bool> useLight, CdBytePattern dark, CdBytePattern light)
    {
        byte[] logical = new byte[length];
        for (int index = 0; index < length; index++)
        {
            logical[index] = (useLight(index) ? light : dark).At(index);
        }

        return logical;
    }

    private static CdSafetyFigures BuildEnvelope()
    {
        int length = DefaultSectors * CdDiscParameters.BytesPerSector;
        List<byte[]> references =
        [
            Fill(length, _ => false, CdBytePattern.Single(0x63), StandardLight),
            Fill(length, _ => false, CdBytePattern.Single(0x10), StandardLight),
        ];
        byte[] fourLevel = new byte[length];
        ReadOnlySpan<byte> palette = [0x0D, 0x10, 0x63, 0xAA];
        for (int index = 0; index < length; index++)
        {
            fourLevel[index] = palette[(int)(ToneDither.Phase(index) * 4)];
        }

        references.Add(fourLevel);
        references.AddRange(PairScenarios(StandardDark, StandardLight, DefaultSectors).Select(scenario => scenario.Logical));
        CdSafetyFigures? envelope = null;
        foreach (byte[] logical in references)
        {
            CdSafetyFigures figures = Measure(logical);
            envelope = envelope is null ? figures : envelope.Max(figures);
        }

        return envelope! with { RunLengthViolations = 0, FalseSyncs = 0 };
    }
}
