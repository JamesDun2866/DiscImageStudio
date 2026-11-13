namespace DiscImageStudio.Cd.Contrast;

/// <summary>
/// 5×7 dot-matrix glyphs for labels drawn into disc data and into legend images. The digit and
/// A/B/E shapes are the ones tools/DvdGrayProbe draws on its DVD test disc; the remaining hex
/// letters and signs follow the same grid.
/// </summary>
internal static class CdDotFont
{
    internal const int GlyphWidth = 5;
    internal const int GlyphHeight = 7;

    /// <summary>Horizontal pitch of one character, including the blank column.</summary>
    internal const int Advance = GlyphWidth + 1;

    private static readonly Dictionary<char, string> Glyphs = new()
    {
        ['0'] = "01110/10001/10011/10101/11001/10001/01110",
        ['1'] = "00100/01100/00100/00100/00100/00100/01110",
        ['2'] = "01110/10001/00001/00010/00100/01000/11111",
        ['3'] = "11110/00001/00001/01110/00001/00001/11110",
        ['4'] = "00010/00110/01010/10010/11111/00010/00010",
        ['5'] = "11111/10000/10000/11110/00001/00001/11110",
        ['6'] = "01110/10000/10000/11110/10001/10001/01110",
        ['7'] = "11111/00001/00010/00100/01000/01000/01000",
        ['8'] = "01110/10001/10001/01110/10001/10001/01110",
        ['9'] = "01110/10001/10001/01111/00001/00001/01110",
        ['A'] = "01110/10001/10001/11111/10001/10001/10001",
        ['B'] = "11110/10001/10001/11110/10001/10001/11110",
        ['C'] = "01110/10001/10000/10000/10000/10001/01110",
        ['D'] = "11110/10001/10001/10001/10001/10001/11110",
        ['E'] = "11111/10000/10000/11110/10000/10000/11111",
        ['F'] = "11111/10000/10000/11110/10000/10000/10000",
        ['-'] = "00000/00000/00000/11111/00000/00000/00000",
        ['+'] = "00000/00100/00100/11111/00100/00100/00000",
        ['%'] = "11001/11010/00010/00100/01000/01011/10011",
        ['.'] = "00000/00000/00000/00000/00000/01100/01100",
        [' '] = "00000/00000/00000/00000/00000/00000/00000",
    };

    /// <summary>True when the dot at column <paramref name="x"/> (0..4) and row <paramref name="y"/>
    /// (0..6, top first) of <paramref name="character"/> is inked. Unknown characters are blank.</summary>
    internal static bool Ink(char character, int x, int y)
    {
        if ((uint)x >= GlyphWidth || (uint)y >= GlyphHeight) return false;
        return Glyphs.TryGetValue(char.ToUpperInvariant(character), out string? rows)
            && rows[(y * (GlyphWidth + 1)) + x] == '1';
    }

    /// <summary>Ink of a text line at dot coordinates relative to its top-left corner.</summary>
    internal static bool Ink(string text, int x, int y)
    {
        if (x < 0 || y < 0) return false;
        int character = x / Advance;
        return character < text.Length && Ink(text[character], x % Advance, y);
    }

    internal static int Width(string text) => text.Length == 0 ? 0 : (text.Length * Advance) - 1;
}
