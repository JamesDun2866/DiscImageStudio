using DiscImageStudio.Core;

namespace DiscImageStudio.Cd;

/// <summary>
/// CD palette encoding of <see cref="ToneProfile"/>. The black/white pair is the higher
/// contrast 0x0D/0xAA pair from upstream 1.1; the 4-level palette adds 0x10 and 0x63 between
/// them. Older tracks drawn with the cdimage mid-tones 0x21 / 0x28 still preview correctly.
/// Custom two-level bytes come from <see cref="CdPalette"/>, which reuses <see cref="MapLevel"/>.
/// </summary>
public static class CdToneMapping
{
    /// <summary>Display grey of the palette bytes; also the tone-mapping endpoints.</summary>
    public const byte BlackGray = 0;
    public const byte WhiteGray = 255;

    private static readonly byte[] TwoLevelPalette = [0x0D, 0xAA];
    private static readonly byte[] FourLevelPalette = [0x0D, 0x10, 0x63, 0xAA];

    public static ReadOnlySpan<byte> Palette(int levels)
        => (levels == ToneProfile.FourLevels ? FourLevelPalette : TwoLevelPalette).AsSpan();

    /// <summary>Maps one source grey to the palette byte that encodes it. The profile must
    /// already be within range (the generators normalize once per job); this runs once per
    /// audio byte, so it deliberately does not re-validate.</summary>
    public static byte Map(byte gray, ToneProfile tone, long sampleIndex)
    {
        if (tone.IsLegacyBinary)
        {
            // Hot path of every existing project: byte-identical to the pre-tone generator.
            return gray < 128 ? TwoLevelPalette[0] : TwoLevelPalette[1];
        }

        return Palette(tone.Levels)[MapLevel(gray, tone, sampleIndex)];
    }

    /// <summary>Tone level (0 = dark … levels − 1 = light) a source grey maps to; the palette
    /// independent half of <see cref="Map"/>.</summary>
    internal static int MapLevel(byte gray, ToneProfile tone, long sampleIndex)
    {
        if (tone.IsLegacyBinary)
        {
            return gray < 128 ? 0 : 1;
        }

        int last = tone.Levels == ToneProfile.FourLevels ? FourLevelPalette.Length - 1 : TwoLevelPalette.Length - 1;
        double position = Math.Clamp(
            (tone.AdjustedGray(gray) - BlackGray) / (double)(WhiteGray - BlackGray),
            0.0,
            1.0) * last;
        int index = (int)position;
        if (index >= last)
        {
            return last;
        }

        double fraction = position - index;
        double threshold = 0.5;
        if (tone.DitherStrength > 0)
        {
            threshold += (tone.DitherStrength > 1 ? 1 : tone.DitherStrength)
                * (ToneDither.Phase(sampleIndex) - 0.5);
        }

        return fraction >= threshold ? index + 1 : index;
    }

    /// <summary>Display grey of a palette byte, so every preview uses the same levels.</summary>
    public static byte DisplayGray(byte paletteByte) => paletteByte switch
    {
        0x0D => 0,
        0x10 => 85,
        0x21 => 96,
        0x28 => 160,
        0x63 => 170,
        0xAA => 255,
        _ => paletteByte,
    };
}
