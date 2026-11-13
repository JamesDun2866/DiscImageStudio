namespace DiscImageStudio.Cd.Physical;

/// <summary>Which merging bits win when two legal choices reach the same |DSV|.</summary>
internal enum CdMergingTieBreak
{
    /// <summary>ECMA-130 Annex E: prefer the combination with a transition.</summary>
    PreferTransition,

    /// <summary>The opposite order, used to bound how much a recorder's own choice can matter.</summary>
    PreferNone,
}

/// <summary>Receives the channel bits of each 588-bit channel frame, in transmission order.</summary>
internal interface ICdChannelSink
{
    void OnChannelFrame(ReadOnlySpan<byte> bits, long frame, CdChannelFrameEvents events);
}

/// <summary>Constraint failures the merging-bit choice could not avoid in one frame.</summary>
internal readonly record struct CdChannelFrameEvents(int RunLengthViolations, int FalseSyncs);

/// <summary>
/// EFM channel coder after ECMA-130 clause 19 and Annex E. A channel frame is the 24-bit Sync
/// Header, then for each of the 33 symbols (subcode, 32 F3 bytes) three merging bits and its
/// 14-bit EFM word, then three merging bits ahead of the next Sync Header: 588 bits.
/// Merging bits are chosen so that every run has 2..10 ZEROs between ONEs, the Sync Header
/// pattern (two consecutive 11T runs) appears nowhere else, and the digital sum value (DSV) at the
/// end of the following word is as close to zero as possible; ties go to the choice with a
/// transition. The bits ahead of the Sync Header are 000 or 100 by the same DSV criterion.
/// A real recorder's encoder may break ties or look ahead differently; this is a reference.
/// </summary>
internal sealed class CdEfmChannel
{
    internal const int FrameBits = 588;
    private const int SymbolBits = 17;
    private static readonly int[] MergingPatterns = [0b000, 0b001, 0b010, 0b100];
    private static readonly int[] SyncMergingPatterns = [0b000, 0b100];

    // 1, ten ZEROs, 1, ten ZEROs, 1, 0 (§19.2). Its third ONE (bit 1) completes the two 11T runs
    // that legitimately form the pattern; two 10-ZERO runs completed anywhere else are a false sync,
    // including a 10-ZERO run ahead of the header followed by the header's first run.
    private const int SyncHeader = 0b100000000001000000000010;
    private const int HeaderPatternEnd = 1;

    private readonly CdMergingTieBreak _tieBreak;
    private readonly ICdChannelSink? _sink;
    private readonly byte[] _frameBits = new byte[FrameBits];
    private long _frame;
    private int _bitPosition;

    // Channel state after the last emitted bit.
    private int _level = -1;
    private long _dsv;
    private int _trailingZeros;
    private int _lastRunZeros = -1;
    private int _runViolations;
    private int _falseSyncs;

    internal CdEfmChannel(CdMergingTieBreak tieBreak = CdMergingTieBreak.PreferTransition, ICdChannelSink? sink = null)
    {
        _tieBreak = tieBreak;
        _sink = sink;
    }

    internal long Dsv => _dsv;

    internal long Frames => _frame;

    /// <summary>Encodes one channel frame: the subcode channel word, then the 32 F3 bytes.</summary>
    internal void WriteFrame(ushort subcodeWord, ReadOnlySpan<byte> f3)
    {
        if (f3.Length != CdCircEncoder.F2Bytes) throw new ArgumentException("F3-frames carry 32 bytes.", nameof(f3));
        _bitPosition = 0;
        _runViolations = 0;
        _falseSyncs = 0;
        if (_frame == 0)
        {
            // Nothing precedes the very first Sync Header; start as if a long run had just ended
            // with a ONE three bits earlier, so the first header is legal with 000.
            _trailingZeros = 2;
            _lastRunZeros = 2;
        }

        AppendChoice(SyncMergingPatterns, SyncHeader, 24, isSync: true);
        AppendChoice(MergingPatterns, subcodeWord, CdEfmTable.WordBits, isSync: false);
        foreach (byte value in f3)
        {
            AppendChoice(MergingPatterns, CdEfmTable.Word(value), CdEfmTable.WordBits, isSync: false);
        }

        // The merging bits ahead of the next Sync Header belong to this 588-bit frame. They are
        // chosen now, against the header that will follow.
        int choice = Choose(SyncMergingPatterns, SyncHeader, 24, isSync: true);
        Emit(choice, 3, isSync: false);

        _sink?.OnChannelFrame(_frameBits, _frame, new CdChannelFrameEvents(_runViolations, _falseSyncs));
        _frame++;
    }

    private void AppendChoice(int[] patterns, int word, int wordBits, bool isSync)
    {
        if (isSync)
        {
            // The merging bits ahead of this header were emitted at the end of the previous
            // frame; the very first frame emits nothing before its header.
            Emit(word, wordBits, isSync: true);
            return;
        }

        int merging = Choose(patterns, word, wordBits, isSync: false);
        Emit(merging, 3, isSync: false);
        Emit(word, wordBits, isSync: false);
    }

    private int Choose(int[] patterns, int word, int wordBits, bool isSync)
    {
        int best = -1;
        long bestDsv = long.MaxValue;
        bool bestHasTransition = false;
        int fallback = patterns[0];
        long fallbackScore = long.MaxValue;
        foreach (int merging in patterns)
        {
            Simulate(merging, word, wordBits, isSync, out long dsv, out int violations, out int falseSyncs);
            long absolute = Math.Abs(dsv);
            if (violations > 0 || falseSyncs > 0)
            {
                // Remember the least bad choice in case no legal combination exists.
                long score = ((violations + falseSyncs) * 1_000_000L) + absolute;
                if (score < fallbackScore)
                {
                    fallbackScore = score;
                    fallback = merging;
                }

                continue;
            }

            bool hasTransition = merging != 0;
            bool better = absolute < bestDsv
                || (absolute == bestDsv && hasTransition != bestHasTransition
                    && hasTransition == (_tieBreak == CdMergingTieBreak.PreferTransition));
            if (best < 0 || better)
            {
                best = merging;
                bestDsv = absolute;
                bestHasTransition = hasTransition;
            }
        }

        return best >= 0 ? best : fallback;
    }

    /// <summary>Runs the merging bits plus the following word on a copy of the channel state.</summary>
    private void Simulate(int merging, int word, int wordBits, bool isSync,
        out long dsv, out int violations, out int falseSyncs)
    {
        int level = _level;
        long sum = _dsv;
        int zeros = _trailingZeros;
        int lastRun = _lastRunZeros;
        violations = 0;
        falseSyncs = 0;
        int combined = (merging << wordBits) | word;
        int bits = wordBits + 3;
        for (int index = bits - 1; index >= 0; index--)
        {
            bool one = ((combined >> index) & 1) != 0;
            if (one)
            {
                if (zeros < 2 || zeros > 10) violations++;
                if (zeros == 10 && lastRun == 10 && !(isSync && index == HeaderPatternEnd)) falseSyncs++;
                lastRun = zeros;
                zeros = 0;
                level = -level;
            }
            else
            {
                zeros++;
                if (zeros > 10) violations++;
            }

            sum += level;
        }

        dsv = sum;
    }

    private void Emit(int value, int bits, bool isSync)
    {
        for (int index = bits - 1; index >= 0; index--)
        {
            bool one = ((value >> index) & 1) != 0;
            if (one)
            {
                if (_trailingZeros < 2 || _trailingZeros > 10) _runViolations++;
                if (_trailingZeros == 10 && _lastRunZeros == 10 && !(isSync && index == HeaderPatternEnd)) _falseSyncs++;
                _lastRunZeros = _trailingZeros;
                _trailingZeros = 0;
                _level = -_level;
            }
            else
            {
                _trailingZeros++;
            }

            _dsv += _level;
            _frameBits[_bitPosition++] = one ? (byte)1 : (byte)0;
        }
    }
}
