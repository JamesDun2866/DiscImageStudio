using System.Globalization;

namespace DiscImageStudio.Cd;

/// <summary>
/// The CD-DA bytes that draw one tone: either a single byte repeated, or two bytes alternating by
/// the parity of the logical byte index. The delay-interleave compensation keeps logical order
/// within each 12-byte half-frame on the disc, so an alternating pattern alternates physically too
/// (checked by the channel-model tests). Text form: "AA" or "F2-44" (hexadecimal).
/// </summary>
public readonly record struct CdBytePattern(byte Even, byte Odd)
{
    public static CdBytePattern Single(byte value) => new(value, value);

    public bool IsSingle => Even == Odd;

    public byte At(long logicalIndex) => (logicalIndex & 1) == 0 ? Even : Odd;

    public override string ToString() => IsSingle
        ? Even.ToString("X2", CultureInfo.InvariantCulture)
        : $"{Even.ToString("X2", CultureInfo.InvariantCulture)}-{Odd.ToString("X2", CultureInfo.InvariantCulture)}";

    public static CdBytePattern Parse(string text)
        => TryParse(text, out CdBytePattern pattern)
            ? pattern
            : throw new FormatException(
                $"'{text}' is not a CD byte pattern; use two hexadecimal digits such as AA, or two bytes such as F2-44.");

    public static bool TryParse(string? text, out CdBytePattern pattern)
    {
        pattern = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] parts = text.Trim().Split(['-', '/', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2) return false;
        Span<byte> values = stackalloc byte[2];
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) part = part[2..];
            if (part.Length is < 1 or > 2
                || !byte.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out values[index]))
            {
                return false;
            }
        }

        pattern = parts.Length == 1 ? Single(values[0]) : new CdBytePattern(values[0], values[1]);
        return true;
    }
}
