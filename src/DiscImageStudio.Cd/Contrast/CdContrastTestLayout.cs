namespace DiscImageStudio.Cd.Contrast;

public enum CdContrastBandRole
{
    /// <summary>Innermost band in the standard dark byte, the "how dark is dark" anchor.</summary>
    DarkAnchor,

    /// <summary>Standard light byte (0xAA) between candidates; carries the next band's number.</summary>
    Reference,

    /// <summary>A light candidate, numbered 1..n from the centre.</summary>
    Candidate,
}

/// <summary>One concentric band of the contrast test disc. Byte offsets are logical CD-DA bytes.</summary>
public sealed record CdContrastBand(
    int Number,
    CdContrastBandRole Role,
    CdBytePattern Pattern,
    double InnerRadiusMm,
    double OuterRadiusMm,
    long FirstByte,
    long EndByte);

/// <summary>
/// Layout of the light-side contrast test disc. The channel model shows the standard dark byte
/// 0x0D is already the darkest texture that stays inside the shipped DC envelope, so the test
/// varies the light tone only. Every candidate is verified safe with 0x0D (CdPatternSafety) and is
/// predicted to have more transitions per millimetre than 0xAA. Each candidate band sits between
/// two 0xAA reference bands, so it is compared with the byte it would replace right next to it.
/// The bands are wide in radius on purpose: one C2 codeword spans about 18 mm of track, and the
/// parity bytes only settle inside a long uniform region.
/// </summary>
public static class CdContrastTestLayout
{
    /// <summary>Name of the candidate list; band numbers keep their meaning within one version.</summary>
    public const string CandidateSet = "cd-contrast-v1";

    internal const double AnchorWidthMm = 2.0;
    internal const double ReferenceWidthMm = 2.0;
    internal const double InnerMarginMm = 0.5;
    internal const double OuterMarginMm = 0.8;
    internal const double MinimumCandidateWidthMm = 1.5;

    public static CdBytePattern Dark { get; } = CdBytePattern.Single(0x0D);

    public static CdBytePattern Reference { get; } = CdBytePattern.Single(0xAA);

    /// <summary>Light candidates by band number (index 0 is band 1), strongest model gain first:
    /// alternating pairs gain about 8–10 % transitions per mm over 0xAA; 0xB6 is the best single
    /// byte (+2 %). 0x44-F2 (+10.7 %) was left out because its gain depends on the recorder's
    /// merging-bit tie-break, and 0xF7-A8 because it leaves the DC envelope with 0x0D.</summary>
    public static IReadOnlyList<CdBytePattern> LightCandidates { get; } =
    [
        CdBytePattern.Parse("F2-A8"),
        CdBytePattern.Parse("A8-F6"),
        CdBytePattern.Parse("F2-44"),
        CdBytePattern.Parse("F2-40"),
        CdBytePattern.Parse("28-F2"),
        CdBytePattern.Parse("B6"),
    ];

    /// <summary>Light bytes of test band <paramref name="number"/> (1-based).</summary>
    public static CdBytePattern LightCandidate(int number)
    {
        if (number < 1 || number > LightCandidates.Count)
            throw new ArgumentOutOfRangeException(nameof(number), $"Contrast test bands are numbered 1..{LightCandidates.Count}.");
        return LightCandidates[number - 1];
    }

    /// <summary>Band number of a light pattern from the current candidate set, or null.</summary>
    public static int? BandOf(CdBytePattern light)
    {
        for (int index = 0; index < LightCandidates.Count; index++)
        {
            if (LightCandidates[index] == light) return index + 1;
        }

        return null;
    }

    public static IReadOnlyList<CdContrastBand> Bands(CdDiscParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        double inner = parameters.InnerRadiusMm;
        double outer = parameters.RadiusFromByte(parameters.TotalBytes);
        double start = inner + InnerMarginMm;
        double end = outer - OuterMarginMm;
        int count = LightCandidates.Count;
        double candidateWidth = (end - start - AnchorWidthMm - ((count + 1) * ReferenceWidthMm)) / count;
        if (candidateWidth < MinimumCandidateWidthMm)
        {
            throw new ArgumentException(
                $"The recording area ({inner:F2}–{outer:F2} mm) is too narrow for {count} contrast bands.",
                nameof(parameters));
        }

        List<(int Number, CdContrastBandRole Role, CdBytePattern Pattern, double Inner, double Outer)> bands = [];
        double radius = start + AnchorWidthMm;
        bands.Add((0, CdContrastBandRole.DarkAnchor, Dark, inner, radius));
        for (int number = 1; number <= count; number++)
        {
            bands.Add((number, CdContrastBandRole.Reference, Reference, radius, radius + ReferenceWidthMm));
            radius += ReferenceWidthMm;
            bands.Add((number, CdContrastBandRole.Candidate, LightCandidates[number - 1], radius, radius + candidateWidth));
            radius += candidateWidth;
        }

        bands.Add((0, CdContrastBandRole.Reference, Reference, radius, outer));

        List<CdContrastBand> result = new(bands.Count);
        long first = 0;
        for (int index = 0; index < bands.Count; index++)
        {
            var band = bands[index];
            long endByte = index == bands.Count - 1 ? parameters.TotalBytes : ByteFromRadius(parameters, band.Outer);
            // Keep whole 2-byte pairs inside one band.
            endByte = Math.Min(parameters.TotalBytes, endByte & ~1L);
            result.Add(new CdContrastBand(band.Number, band.Role, band.Pattern, band.Inner, band.Outer, first, endByte));
            first = endByte;
        }

        return result;
    }

    /// <summary>First logical byte at or beyond <paramref name="radiusMm"/> (bisection on the
    /// generator's own radius function, so cubic-pitch geometry is honoured too).</summary>
    internal static long ByteFromRadius(CdDiscParameters parameters, double radiusMm)
    {
        long low = 0;
        long high = parameters.GeometryBytes;
        while (low < high)
        {
            long middle = low + ((high - low) / 2);
            if (parameters.RadiusFromByte(middle) < radiusMm) low = middle + 1;
            else high = middle;
        }

        return low;
    }
}
