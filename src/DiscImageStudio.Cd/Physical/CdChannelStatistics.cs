using System.Numerics;

namespace DiscImageStudio.Cd.Physical;

/// <summary>
/// Texture and low-frequency figures of an EFM channel bit stream (NRZI: a ONE is a transition
/// between mark and space). Frames before <see cref="WarmupFrames"/> keep the running level and
/// DSV continuous but are not counted, so the CIRC delay lines and the start of a track do not
/// bias the result. Lengths assume the CD channel rate 4.3218 Mbit/s at the given scanning speed.
/// </summary>
internal sealed class CdChannelStatistics : ICdChannelSink
{
    internal const double ChannelBitRate = 4_321_800.0;
    internal const double LowFrequencyLimitHz = 20_000.0;
    private const int SpectrumSegment = 16_384;
    private const int ShortWindow = 216;
    private const int FrameWindow = CdEfmChannel.FrameBits;

    private readonly bool _spectrum;
    private readonly double _bitLengthMm;
    private readonly long[] _runs = new long[12];
    private readonly long[] _dsvHistory = new long[FrameWindow + 1];
    private readonly double[] _segment;
    private readonly double[] _powerSum;
    private int _segmentFill;
    private int _segments;
    private double _biasSquareSum;
    private long _biasSamples;

    private int _level = -1;
    private long _dsv;
    private long _historyCount;
    private int _zeros;
    private bool _runStarted;

    internal CdChannelStatistics(int warmupFrames, double velocityMmPerSecond = 1200.0, bool spectrum = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(warmupFrames);
        WarmupFrames = warmupFrames;
        _spectrum = spectrum;
        _bitLengthMm = velocityMmPerSecond / ChannelBitRate;
        _segment = spectrum ? new double[SpectrumSegment] : [];
        _powerSum = spectrum ? new double[(SpectrumSegment / 2) + 1] : [];
    }

    internal int WarmupFrames { get; }
    internal long Frames { get; private set; }
    internal long Bits { get; private set; }
    internal long Transitions { get; private set; }
    internal long HighBits { get; private set; }
    internal long RunLengthViolations { get; private set; }
    internal long FalseSyncs { get; private set; }
    internal long MaxAbsDsv { get; private set; }
    internal long MinDsv { get; private set; } = long.MaxValue;
    internal long MaxDsv { get; private set; } = long.MinValue;
    internal double MaxBias216 { get; private set; }
    internal double MaxBias588 { get; private set; }

    /// <summary>Completed runs by length in channel bits (3T..11T at indices 3..11).</summary>
    internal ReadOnlySpan<long> RunHistogram => _runs;

    internal double TransitionsPerBit => Bits == 0 ? 0 : (double)Transitions / Bits;

    internal double MeanRunT => Transitions == 0 ? 0 : (double)Bits / Transitions;

    internal double TransitionsPerMm => Bits == 0 ? 0 : Transitions / (Bits * _bitLengthMm);

    internal double MarkFraction => Bits == 0 ? 0 : (double)HighBits / Bits;

    /// <summary>Cheap low-frequency estimate: mean power of the NRZI signal after a 216-bit
    /// moving average (first null at 4.3218 MHz / 216 = 20 kHz). Used where the full spectrum
    /// would be too slow, such as safety checks over many textures.</summary>
    internal double LowFrequencyProxy => _biasSamples == 0 ? 0 : _biasSquareSum / _biasSamples;

    /// <summary>Welch estimate (Hann window, no mean removal) of the share of signal power
    /// below 20 kHz at 1× speed. NaN when the spectrum was not requested or is too short.</summary>
    internal double LowFrequencyFraction
    {
        get
        {
            if (!_spectrum || _segments == 0) return double.NaN;
            int limit = (int)Math.Floor(LowFrequencyLimitHz * SpectrumSegment / ChannelBitRate);
            double low = 0, total = 0;
            for (int bin = 0; bin < _powerSum.Length; bin++)
            {
                total += _powerSum[bin];
                if (bin <= limit) low += _powerSum[bin];
            }

            return total == 0 ? 0 : low / total;
        }
    }

    /// <summary>Along-track period of the strongest spectral line above 20 kHz, in µm, and its
    /// share of the power; a strongly periodic texture acts as a diffraction grating.</summary>
    internal (double PeriodUm, double PowerFraction) DominantPeriod
    {
        get
        {
            if (!_spectrum || _segments == 0) return (double.NaN, double.NaN);
            int limit = (int)Math.Floor(LowFrequencyLimitHz * SpectrumSegment / ChannelBitRate);
            double total = 0, best = -1;
            int bestBin = 0;
            for (int bin = 0; bin < _powerSum.Length; bin++)
            {
                total += _powerSum[bin];
                if (bin > limit && _powerSum[bin] > best)
                {
                    best = _powerSum[bin];
                    bestBin = bin;
                }
            }

            double periodBits = (double)SpectrumSegment / bestBin;
            return (periodBits * _bitLengthMm * 1000.0, total == 0 ? 0 : best / total);
        }
    }

    public void OnChannelFrame(ReadOnlySpan<byte> bits, long frame, CdChannelFrameEvents events)
    {
        bool counted = frame >= WarmupFrames;
        if (counted)
        {
            Frames++;
            RunLengthViolations += events.RunLengthViolations;
            FalseSyncs += events.FalseSyncs;
        }

        foreach (byte bit in bits)
        {
            if (bit != 0)
            {
                _level = -_level;
                if (counted)
                {
                    Transitions++;
                    if (_runStarted)
                    {
                        int length = _zeros + 1;
                        _runs[Math.Min(length, _runs.Length - 1)]++;
                    }
                }

                _runStarted = counted;
                _zeros = 0;
            }
            else
            {
                _zeros++;
            }

            _dsv += _level;
            if (!counted)
            {
                continue;
            }

            Bits++;
            if (_level > 0) HighBits++;
            long absolute = Math.Abs(_dsv);
            if (absolute > MaxAbsDsv) MaxAbsDsv = absolute;
            if (_dsv < MinDsv) MinDsv = _dsv;
            if (_dsv > MaxDsv) MaxDsv = _dsv;

            _dsvHistory[_historyCount % _dsvHistory.Length] = _dsv;
            _historyCount++;
            if (_historyCount > ShortWindow)
            {
                long earlier = _dsvHistory[(_historyCount - 1 - ShortWindow) % _dsvHistory.Length];
                double bias = (_dsv - earlier) / (double)ShortWindow;
                MaxBias216 = Math.Max(MaxBias216, Math.Abs(bias));
                _biasSquareSum += bias * bias;
                _biasSamples++;
            }

            if (_historyCount > FrameWindow)
            {
                long earlier = _dsvHistory[(_historyCount - 1 - FrameWindow) % _dsvHistory.Length];
                MaxBias588 = Math.Max(MaxBias588, Math.Abs(_dsv - earlier) / (double)FrameWindow);
            }

            if (_spectrum)
            {
                _segment[_segmentFill++] = _level;
                if (_segmentFill == SpectrumSegment)
                {
                    AccumulateSegment();
                    // 50 % overlap.
                    Array.Copy(_segment, SpectrumSegment / 2, _segment, 0, SpectrumSegment / 2);
                    _segmentFill = SpectrumSegment / 2;
                }
            }
        }
    }

    private void AccumulateSegment()
    {
        Complex[] data = new Complex[SpectrumSegment];
        for (int index = 0; index < SpectrumSegment; index++)
        {
            double window = 0.5 - (0.5 * Math.Cos(2 * Math.PI * index / (SpectrumSegment - 1)));
            data[index] = new Complex(_segment[index] * window, 0);
        }

        Fft(data);
        for (int bin = 0; bin < _powerSum.Length; bin++)
        {
            double power = data[bin].Magnitude * data[bin].Magnitude;
            _powerSum[bin] += bin is 0 or SpectrumSegment / 2 ? power : 2 * power;
        }

        _segments++;
    }

    private static void Fft(Complex[] data)
    {
        int n = data.Length;
        for (int index = 1, reversed = 0; index < n; index++)
        {
            int bit = n >> 1;
            for (; (reversed & bit) != 0; bit >>= 1) reversed ^= bit;
            reversed ^= bit;
            if (index < reversed) (data[index], data[reversed]) = (data[reversed], data[index]);
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = -2 * Math.PI / length;
            Complex step = new(Math.Cos(angle), Math.Sin(angle));
            for (int start = 0; start < n; start += length)
            {
                Complex twiddle = Complex.One;
                for (int offset = 0; offset < length / 2; offset++)
                {
                    Complex even = data[start + offset];
                    Complex odd = data[start + offset + (length / 2)] * twiddle;
                    data[start + offset] = even + odd;
                    data[start + offset + (length / 2)] = even - odd;
                    twiddle *= step;
                }
            }
        }
    }
}
