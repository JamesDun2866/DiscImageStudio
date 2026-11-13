using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscImageStudio.Cd.Physical;

/// <summary>Channel figures of one uniform texture (steady state, both tie-break orders).</summary>
internal sealed record CdPatternFigures(
    string Pattern,
    double TransitionsPerMm,
    double MeanRunT,
    IReadOnlyDictionary<string, double> RunShare,
    long MaxAbsDsv,
    double LowFrequencyProxy,
    double TieBreakSpread,
    bool PureFillSafe);

internal sealed record CdCandidateFigures(
    string Pattern,
    string PairedWith,
    double TransitionsPerMm,
    double ChangeVersusStandard,
    bool PairSafe,
    string Worst,
    string? FailingScenario);

internal sealed record CdChannelReportDocument(
    string Format,
    string Model,
    double VelocityMmPerSecond,
    string Envelope,
    IReadOnlyList<CdPatternFigures> Patterns,
    IReadOnlyList<CdCandidateFigures> DarkCandidates,
    IReadOnlyList<CdCandidateFigures> LightCandidates);

/// <summary>
/// Ranks byte patterns by the texture the reference channel model predicts on the disc:
/// fewer transitions per millimetre (longer marks and spaces) for the dark tone, more for the
/// light tone. Parity and subcode symbols are included, because they fill 9 of every 33 symbols.
/// Candidates are kept only when every texture they form with the opposite standard byte stays
/// inside the shipped palette's envelope (<see cref="CdPatternSafety"/>).
/// </summary>
internal static class CdChannelReport
{
    internal const string FormatName = "cd-channel-report-v1";

    internal static CdChannelReportDocument Build(
        IReadOnlyList<CdBytePattern>? patterns,
        bool screenPairs,
        int candidates,
        double velocityMmPerSecond,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(candidates, 1);
        CdChannelOptions steady = new(Sectors: 6, WarmupFrames: CdSubcode.FramesPerSection,
            VelocityMmPerSecond: velocityMmPerSecond);
        CdSafetyFigures envelope = CdPatternSafety.Envelope;
        IReadOnlyList<CdBytePattern> evaluated = patterns ?? Enumerable.Range(0, 256)
            .Select(value => CdBytePattern.Single((byte)value)).ToArray();
        List<CdPatternFigures> figures = Evaluate(evaluated, steady, envelope, cancellationToken);

        if (screenPairs)
        {
            // Pairs only help where single bytes run out of room: alternate the strongest singles.
            List<CdPatternFigures> safeSingles = figures.Where(figure => figure.PureFillSafe
                && CdBytePattern.Parse(figure.Pattern).IsSingle).ToList();
            List<CdBytePattern> pairs = [];
            foreach (bool lightPool in new[] { false, true })
            {
                List<CdBytePattern> pool = (lightPool
                        ? safeSingles.OrderByDescending(figure => figure.TransitionsPerMm)
                        : safeSingles.OrderBy(figure => figure.TransitionsPerMm))
                    .Take(24).Select(figure => CdBytePattern.Parse(figure.Pattern)).ToList();
                foreach (CdBytePattern first in pool)
                {
                    foreach (CdBytePattern second in pool)
                    {
                        if (first.Even != second.Even) pairs.Add(new CdBytePattern(first.Even, second.Even));
                    }
                }
            }

            figures.AddRange(Evaluate(pairs, steady, envelope, cancellationToken));
        }

        CdPatternFigures standardDark = figures.FirstOrDefault(figure => figure.Pattern == "0D")
            ?? Evaluate([CdPatternSafety.StandardDark], steady, envelope, cancellationToken)[0];
        CdPatternFigures standardLight = figures.FirstOrDefault(figure => figure.Pattern == "AA")
            ?? Evaluate([CdPatternSafety.StandardLight], steady, envelope, cancellationToken)[0];

        List<CdCandidateFigures> dark = Rank(figures, ascending: true, standardDark, CdPatternSafety.StandardLight,
            candidates, cancellationToken);
        List<CdCandidateFigures> light = Rank(figures, ascending: false, standardLight, CdPatternSafety.StandardDark,
            candidates, cancellationToken);
        return new CdChannelReportDocument(
            FormatName,
            "ECMA-130 reference: generator interleave, CIRC C2/C1 with inverted parity, subcode, "
                + "EFM with Annex E merging bits (both tie-break orders); steady-state uniform textures.",
            velocityMmPerSecond,
            envelope.ToString(),
            figures,
            dark,
            light);
    }

    private static List<CdPatternFigures> Evaluate(IReadOnlyList<CdBytePattern> patterns, CdChannelOptions options,
        CdSafetyFigures envelope, CancellationToken cancellationToken)
    {
        CdPatternFigures[] results = new CdPatternFigures[patterns.Count];
        Parallel.For(0, patterns.Count, new ParallelOptions { CancellationToken = cancellationToken }, index =>
        {
            CdBytePattern pattern = patterns[index];
            CdChannelStatistics preferred = CdChannelModel.AnalyzeSteadyState(pattern, options);
            CdChannelStatistics other = CdChannelModel.AnalyzeSteadyState(pattern,
                options with { TieBreak = CdMergingTieBreak.PreferNone });
            CdSafetyFigures pure = CdSafetyFigures.Of(preferred).Max(CdSafetyFigures.Of(other));
            Dictionary<string, double> share = new(StringComparer.Ordinal);
            long runs = 0;
            for (int length = 3; length <= 11; length++) runs += preferred.RunHistogram[length];
            for (int length = 3; length <= 11; length++)
            {
                share[$"{length}T"] = runs == 0 ? 0 : Math.Round((double)preferred.RunHistogram[length] / runs, 4);
            }

            results[index] = new CdPatternFigures(
                pattern.ToString(),
                Math.Round(preferred.TransitionsPerMm, 1),
                Math.Round(preferred.MeanRunT, 3),
                share,
                pure.MaxAbsDsv,
                Math.Round(pure.LowFrequencyProxy, 6),
                Math.Round(Math.Abs(preferred.TransitionsPerMm - other.TransitionsPerMm), 1),
                pure.Within(envelope));
        });
        return [.. results];
    }

    private static List<CdCandidateFigures> Rank(List<CdPatternFigures> figures, bool ascending,
        CdPatternFigures standard, CdBytePattern opposite, int count, CancellationToken cancellationToken)
    {
        IEnumerable<CdPatternFigures> ordered = figures.Where(figure => figure.PureFillSafe && figure.Pattern != standard.Pattern);
        ordered = ascending
            ? ordered.OrderBy(figure => figure.TransitionsPerMm).ThenBy(figure => figure.Pattern, StringComparer.Ordinal)
            : ordered.OrderByDescending(figure => figure.TransitionsPerMm).ThenBy(figure => figure.Pattern, StringComparer.Ordinal);
        List<CdCandidateFigures> result = [];
        HashSet<string> textures = [];
        foreach (CdPatternFigures figure in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Several bytes share one run-length texture; keep one of each.
            string texture = string.Join(',', figure.RunShare.Values.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)));
            if (!textures.Add(texture)) continue;
            CdBytePattern pattern = CdBytePattern.Parse(figure.Pattern);
            CdPairAssessment assessment = ascending
                ? CdPatternSafety.AssessPair(pattern, opposite)
                : CdPatternSafety.AssessPair(opposite, pattern);
            if (!assessment.Safe) continue;
            result.Add(new CdCandidateFigures(
                figure.Pattern,
                opposite.ToString(),
                figure.TransitionsPerMm,
                Math.Round((figure.TransitionsPerMm / standard.TransitionsPerMm) - 1, 4),
                assessment.Safe,
                assessment.Worst.ToString(),
                assessment.FailingScenario));
            if (result.Count == count) break;
        }

        return result;
    }

    internal static void Save(CdChannelReportDocument document, string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (Path.GetExtension(fullPath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            StringBuilder csv = new();
            csv.AppendLine("pattern,transitions_per_mm,mean_run_t,max_abs_dsv,lf_proxy,tie_break_spread,pure_fill_safe,"
                + string.Join(',', Enumerable.Range(3, 9).Select(length => $"share_{length}t")));
            foreach (CdPatternFigures figure in document.Patterns)
            {
                csv.Append(CultureInfo.InvariantCulture,
                    $"{figure.Pattern},{figure.TransitionsPerMm},{figure.MeanRunT},{figure.MaxAbsDsv},{figure.LowFrequencyProxy},{figure.TieBreakSpread},{figure.PureFillSafe},");
                csv.AppendLine(string.Join(',', figure.RunShare.Values.Select(value => value.ToString(CultureInfo.InvariantCulture))));
            }

            File.WriteAllText(fullPath, csv.ToString());
            return;
        }

        File.WriteAllText(fullPath, JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
