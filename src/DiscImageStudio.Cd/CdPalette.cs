using DiscImageStudio.Core;

namespace DiscImageStudio.Cd;

/// <summary>
/// The CD-DA bytes that draw the dark and the light tone of a two-level picture. The standard
/// palette is the shipped 0x0D / 0xAA pair; its generation path is unchanged byte for byte.
/// A custom palette may use alternating byte pairs (<see cref="CdBytePattern"/>); the tone decision
/// is then made once per even/odd byte pair, so a pair is never split between the two tones.
/// Custom palettes are two-level and audio-only: the 4-level mid-tones and the Mode 1 data path
/// keep the standard bytes.
/// </summary>
public sealed record CdPalette(CdBytePattern Dark, CdBytePattern Light)
{
    public static CdPalette Standard { get; } = new(CdBytePattern.Single(0x0D), CdBytePattern.Single(0xAA));

    public bool IsStandard => Dark == Standard.Dark && Light == Standard.Light;

    /// <summary>True when the tone is decided per even/odd byte pair instead of per byte.</summary>
    public bool DecidesPerPair => !Dark.IsSingle || !Light.IsSingle;

    /// <summary>Byte of tone level 0 (dark) or 1 (light) at a logical byte index.</summary>
    public byte Encode(int level, long logicalIndex) => (level == 0 ? Dark : Light).At(logicalIndex);

    public void Validate(int toneLevels)
    {
        if (IsStandard)
        {
            return;
        }

        if (toneLevels != ToneProfile.TwoLevels)
        {
            throw new ArgumentException("Custom CD disc bytes work with 2 tone levels only.", nameof(toneLevels));
        }

        if (Dark.Even == Light.Even || Dark.Odd == Light.Odd)
        {
            throw new ArgumentException(
                $"Dark bytes {Dark} and light bytes {Light} must differ at every byte position.");
        }
    }

    /// <summary>Grey a read-back preview shows for <paramref name="value"/> at a logical index: the
    /// palette's own bytes first, then the standard table so older tracks still preview.</summary>
    public byte DisplayGray(byte value, long logicalIndex)
    {
        if (!IsStandard)
        {
            if (value == Light.At(logicalIndex)) return CdToneMapping.WhiteGray;
            if (value == Dark.At(logicalIndex)) return CdToneMapping.BlackGray;
        }

        return CdToneMapping.DisplayGray(value);
    }

    public override string ToString() => $"{Dark}/{Light}";

    /// <summary>Builds a palette from optional texts; a missing side keeps its standard bytes.</summary>
    public static CdPalette FromText(string? dark, string? light)
        => new(string.IsNullOrWhiteSpace(dark) ? Standard.Dark : CdBytePattern.Parse(dark),
            string.IsNullOrWhiteSpace(light) ? Standard.Light : CdBytePattern.Parse(light));
}

/// <summary>Per-job encoder of a custom palette. It is a mutable struct: keep one per sequential
/// byte stream, because the odd byte of a pair reuses the decision of the even byte before it.</summary>
internal struct CdPaletteEncoder(CdPalette palette, ToneProfile tone)
{
    private int _pairLevel;
    private long _pairIndex = -1;

    internal byte Encode(byte gray, long logicalIndex)
    {
        int level;
        if (palette.DecidesPerPair && (logicalIndex & 1) == 1 && _pairIndex == logicalIndex - 1)
        {
            level = _pairLevel;
        }
        else
        {
            level = CdToneMapping.MapLevel(gray, tone, palette.DecidesPerPair ? logicalIndex & ~1L : logicalIndex);
            _pairLevel = level;
            _pairIndex = logicalIndex;
        }

        return palette.Encode(level, logicalIndex);
    }
}
