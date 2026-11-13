namespace DiscImageStudio.Cd.Physical;

/// <summary>
/// Reference CIRC encoder after ECMA-130 Annex C (identical to IEC 908). It turns each 24-byte
/// F1-frame into a 32-byte F2-frame:
/// <list type="number">
/// <item>First delay section: the 24 bytes form 12 two-byte words (byte 2k is word k's A byte,
/// 2k+1 its B byte). Words 0, 4, 8, 1, 5, 9 enter C2 positions 0–11 two frames late; words
/// 2, 6, 10, 3, 7, 11 enter positions 16–27 undelayed (figure C.3, V_Q).</item>
/// <item>C2 (28,24) puts its Q parity at positions 12–15.</item>
/// <item>Second delay section: C1 position j takes C2 position j from j·D frames ago, D = 4.</item>
/// <item>C1 (32,28) appends its P parity at positions 28–31.</item>
/// <item>Third delay section: every even output position is delayed one more frame.</item>
/// <item>All P and Q bits are inverted on output.</item>
/// </list>
/// The delay lines start filled with zero bytes. The resulting slot/delay table is the one in
/// figure C.4 (shortest data delay 3 frames, longest 108).
/// </summary>
internal sealed class CdCircEncoder
{
    internal const int F1Bytes = 24;
    internal const int F2Bytes = 32;
    internal const int Depth = 4;

    /// <summary>C2 words in order of their C2 positions 0–11 (delayed) and 16–27 (undelayed).</summary>
    internal static ReadOnlySpan<int> DelayedWords => [0, 4, 8, 1, 5, 9];
    internal static ReadOnlySpan<int> UndelayedWords => [2, 6, 10, 3, 7, 11];

    private const int C2History = (CdCircParity.C1Length - CdCircParity.ParityCount - 1) * Depth + 1;

    private readonly byte[][] _f1History = [new byte[F1Bytes], new byte[F1Bytes], new byte[F1Bytes]];
    private readonly byte[][] _c2History;
    private readonly byte[] _previousC1 = new byte[CdCircParity.C1Length];
    private readonly byte[] _c1 = new byte[CdCircParity.C1Length];
    private long _frame;

    internal CdCircEncoder()
    {
        _c2History = new byte[C2History][];
        for (int index = 0; index < _c2History.Length; index++)
        {
            _c2History[index] = new byte[CdCircParity.C2Length];
        }
    }

    /// <summary>Number of F1-frames encoded so far.</summary>
    internal long Frames => _frame;

    internal void Encode(ReadOnlySpan<byte> f1, Span<byte> f2)
    {
        if (f1.Length != F1Bytes) throw new ArgumentException("F1-frames have 24 bytes.", nameof(f1));
        if (f2.Length != F2Bytes) throw new ArgumentException("F2-frames have 32 bytes.", nameof(f2));

        byte[] current = _f1History[_frame % 3];
        f1.CopyTo(current);
        byte[] twoFramesAgo = _f1History[(_frame + 1) % 3];
        if (_frame < 2)
        {
            Array.Clear(twoFramesAgo);
        }

        byte[] c2 = _c2History[_frame % C2History];
        for (int index = 0; index < 6; index++)
        {
            int delayedWord = DelayedWords[index];
            c2[2 * index] = twoFramesAgo[2 * delayedWord];
            c2[(2 * index) + 1] = twoFramesAgo[(2 * delayedWord) + 1];
            int undelayedWord = UndelayedWords[index];
            c2[16 + (2 * index)] = current[2 * undelayedWord];
            c2[17 + (2 * index)] = current[(2 * undelayedWord) + 1];
        }

        CdCircParity.EncodeC2(c2);

        for (int position = 0; position < CdCircParity.C2Length; position++)
        {
            long source = _frame - (position * Depth);
            _c1[position] = source < 0 ? (byte)0 : _c2History[source % C2History][position];
        }

        CdCircParity.EncodeC1(_c1);

        for (int slot = 0; slot < F2Bytes; slot++)
        {
            byte value = (slot & 1) == 0 ? _previousC1[slot] : _c1[slot];
            bool parity = slot is >= CdCircParity.QStart and < CdCircParity.QStart + CdCircParity.ParityCount
                or >= CdCircParity.PStart;
            f2[slot] = parity ? (byte)~value : value;
        }

        _c1.CopyTo(_previousC1, 0);
        _frame++;
    }

    /// <summary>F2 slot of a C1 data position: positions 0–11 are slots 0–11, 12–23 are 16–27.</summary>
    internal static bool IsDataSlot(int slot)
        => slot is >= 0 and < CdCircParity.QStart or >= CdCircParity.QStart + CdCircParity.ParityCount and < CdCircParity.PStart;
}
