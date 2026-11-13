namespace DiscImageStudio.Cd.Physical;

/// <summary>EFM 8-to-14 modulation table (ECMA-130 Annex D) and the two subcode sync patterns.</summary>
internal static partial class CdEfmTable
{
    internal const int WordBits = 14;

    /// <summary>SYNC 0, the subcode byte of the first frame of each 98-frame section (§19.1).</summary>
    internal const ushort Sync0 = 0b00100000000001;

    /// <summary>SYNC 1, the subcode byte of the second frame of each section (§19.1).</summary>
    internal const ushort Sync1 = 0b00000000010010;

    internal static ushort Word(byte value) => WordsTable[value];

    internal static ReadOnlySpan<ushort> Words => WordsTable;
}
