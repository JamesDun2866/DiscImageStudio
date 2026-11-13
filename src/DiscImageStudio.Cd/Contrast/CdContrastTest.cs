using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscImageStudio.Cd.Physical;
using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Cd.Contrast;

public sealed record CdContrastTestResult(
    string TrackPath,
    string? CueSheetPath,
    string LegendJsonPath,
    string LegendPngPath,
    IReadOnlyList<CdContrastBand> Bands,
    CdGenerationSummary Summary);

/// <summary>Model prediction stored in the legend: transitions per mm relative to 0xAA.</summary>
public sealed record CdContrastPrediction(int Band, string Bytes, double TransitionsPerMm, double ChangeVersusStandard);

/// <summary>
/// Generates the light-side contrast test disc (<see cref="CdContrastTestLayout"/>): concentric
/// bands written with exact bytes (no tone mapping), the band numbers drawn as 5×7 dot glyphs in
/// 0x0D inside each 0xAA reference band at 12, 4 and 8 o'clock, plus a legend (JSON and PNG).
/// </summary>
public static class CdContrastTest
{
    public const string LegendFormat = "cd-contrast-test-v1";
    private const double LabelDotMm = 0.2;
    private static readonly double[] LabelAngles = [0, 2 * Math.PI / 3, 4 * Math.PI / 3];

    public static CdContrastTestResult Generate(
        string outputPath,
        CdDiscParameters parameters,
        bool interleave = true,
        bool writeCue = true,
        Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        parameters.Validate();
        string target = Path.GetFullPath(outputPath);
        string extension = Path.GetExtension(target);
        bool wave = extension.Equals(".wav", StringComparison.OrdinalIgnoreCase);
        if (!wave && !extension.Equals(".raw", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The contrast test track must be .wav (PCM audio + CUE) or .raw.", nameof(outputPath));
        if (wave) CdWaveFile.ValidateLength(parameters.TotalBytes);
        IReadOnlyList<CdContrastBand> bands = CdContrastTestLayout.Bands(parameters);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        CdGenerationSummary summary;
        using (FileStream file = new(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024,
            FileOptions.SequentialScan))
        {
            if (wave) CdWaveFile.WriteHeader(file, parameters.TotalBytes);
            summary = GenerateToStream(file, parameters, interleave,
                wave ? CdAudioByteOrder.LittleEndian : CdAudioByteOrder.BigEndian, progress, cancellationToken, target);
        }

        CdTrackMetadata.Save(target, new CdTrackMetadata(
            $"{CdContrastTestLayout.CandidateSet} contrast test", target, parameters.InnerRadiusMm, parameters.OuterRadiusMm,
            parameters.Sectors, parameters.LinearVelocityMmPerSecond, parameters.StartAngleRadians * 180.0 / Math.PI,
            parameters.ImageOuterRadiusMm, interleave, PitchLinear: parameters.PitchLinear,
            PitchQuadratic: parameters.PitchQuadratic, PitchCubic: parameters.PitchCubic,
            UsableSectors: parameters.UsableSectors, SourceImageOuterRadiusMm: parameters.SourceImageOuterRadiusMm));
        string? cue = wave && writeCue ? CdWaveFile.WriteCue(target) : null;
        string legendJson = target + ".contrast.json";
        string legendPng = target + ".legend.png";
        IReadOnlyList<CdContrastPrediction> predictions = Predictions(parameters.LinearVelocityMmPerSecond);
        File.WriteAllText(legendJson, JsonSerializer.Serialize(new LegendDocument(
            LegendFormat, CdContrastTestLayout.CandidateSet, CdContrastTestLayout.Dark.ToString(),
            CdContrastTestLayout.Reference.ToString(), parameters.InnerRadiusMm, parameters.OuterRadiusMm,
            parameters.OutputSectors, interleave,
            [.. bands.Select(band => new LegendBand(band.Number, band.Role.ToString(), band.Pattern.ToString(),
                Math.Round(band.InnerRadiusMm, 2), Math.Round(band.OuterRadiusMm, 2)))],
            predictions,
            "Look at the shiny side reflecting a bright, even light. Each numbered band sits between two "
                + "standard light (AA) bands; pick the band that stands out most against its neighbours, or "
                + "keep the standard bytes if none does."), JsonOptions) + Environment.NewLine);
        SaveLegendPng(legendPng, bands, parameters, predictions);
        return new CdContrastTestResult(target, cue, legendJson, legendPng, bands, summary with { CueSheetPath = cue });
    }

    /// <summary>Writes the test track without a file header (for streaming burns).</summary>
    public static CdGenerationSummary GenerateToStream(
        Stream output,
        CdDiscParameters parameters,
        bool interleave = true,
        CdAudioByteOrder audioByteOrder = CdAudioByteOrder.BigEndian,
        Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string outputDescription = "direct-burn-stream")
    {
        ArgumentNullException.ThrowIfNull(output);
        parameters.Validate();
        Sampler sampler = new(parameters, CdContrastTestLayout.Bands(parameters));
        return CdTrackGenerator.GenerateBytesToStream(sampler.ByteAt, $"{CdContrastTestLayout.CandidateSet} contrast test",
            output, parameters, interleave, progress, cancellationToken, outputDescription, audioByteOrder);
    }

    /// <summary>Channel-model prediction for every candidate, relative to the standard light byte.</summary>
    public static IReadOnlyList<CdContrastPrediction> Predictions(double velocityMmPerSecond = 1200.0)
    {
        CdChannelOptions options = new(Sectors: 6, WarmupFrames: CdSubcode.FramesPerSection,
            VelocityMmPerSecond: velocityMmPerSecond);
        double standard = CdChannelModel.AnalyzeSteadyState(CdContrastTestLayout.Reference, options).TransitionsPerMm;
        List<CdContrastPrediction> predictions = [];
        for (int number = 1; number <= CdContrastTestLayout.LightCandidates.Count; number++)
        {
            CdBytePattern pattern = CdContrastTestLayout.LightCandidate(number);
            double density = CdChannelModel.AnalyzeSteadyState(pattern, options).TransitionsPerMm;
            predictions.Add(new CdContrastPrediction(number, pattern.ToString(), Math.Round(density, 1),
                Math.Round((density / standard) - 1, 4)));
        }

        return predictions;
    }

    /// <summary>Stateless byte lookup, safe to call from the generator's parallel workers.</summary>
    internal sealed class Sampler(CdDiscParameters parameters, IReadOnlyList<CdContrastBand> bands)
    {
        private readonly CdContrastBand[] _bands = [.. bands];

        internal byte ByteAt(long logicalByte)
        {
            CdContrastBand band = Find(logicalByte);
            if (band.Role == CdContrastBandRole.Reference && band.Number > 0 && IsLabelInk(band, logicalByte))
            {
                return CdContrastTestLayout.Dark.At(logicalByte);
            }

            return band.Pattern.At(logicalByte);
        }

        private CdContrastBand Find(long logicalByte)
        {
            int low = 0;
            int high = _bands.Length - 1;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (logicalByte >= _bands[middle].EndByte) low = middle + 1;
                else high = middle;
            }

            return _bands[low];
        }

        private bool IsLabelInk(CdContrastBand band, long logicalByte)
        {
            (double radius, double theta) = parameters.PolarFromByte(logicalByte);
            string text = band.Number.ToString(CultureInfo.InvariantCulture);
            double halfWidth = (CdDotFont.Width(text) * LabelDotMm / 2) + LabelDotMm;
            double centre = (band.InnerRadiusMm + band.OuterRadiusMm) / 2;
            double x = radius * Math.Cos(theta);
            double y = radius * Math.Sin(theta);
            foreach (double clockwise in LabelAngles)
            {
                // Labels are laid out in image coordinates (x right, y down) like the DVD probe's:
                // "up" points away from the centre, angles run clockwise from 12 o'clock.
                double direction = clockwise - (Math.PI / 2);
                double delta = Math.IEEERemainder(theta - direction, 2 * Math.PI);
                if (Math.Abs(delta) * radius > halfWidth + LabelDotMm) continue;
                double dv = (x * Math.Sin(clockwise)) - (y * Math.Cos(clockwise)) - centre;
                double u = (x * Math.Cos(clockwise)) + (y * Math.Sin(clockwise));
                int column = (int)Math.Floor((u + (CdDotFont.Width(text) * LabelDotMm / 2)) / LabelDotMm);
                int row = (int)Math.Floor(3.5 - (dv / LabelDotMm));
                if (CdDotFont.Ink(text, column, row)) return true;
            }

            return false;
        }
    }

    private static void SaveLegendPng(string path, IReadOnlyList<CdContrastBand> bands, CdDiscParameters parameters,
        IReadOnlyList<CdContrastPrediction> predictions)
    {
        const int size = 1200;
        double outerMm = parameters.RadiusFromByte(parameters.TotalBytes) + 1.0;
        double scale = size / 2.0 / outerMm;
        uint[] candidateColours = [0x2E86C1, 0x28B463, 0xD68910, 0xC0392B, 0x8E44AD, 0x16A085];
        byte[] pixels = new byte[size * size * 4];
        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                double x = (px + 0.5 - (size / 2.0)) / scale;
                double y = (py + 0.5 - (size / 2.0)) / scale;
                double radius = Math.Sqrt((x * x) + (y * y));
                uint colour = 0xF4F6F7;
                CdContrastBand? band = bands.FirstOrDefault(candidate =>
                    radius >= candidate.InnerRadiusMm && radius < candidate.OuterRadiusMm);
                if (band is not null)
                {
                    colour = band.Role switch
                    {
                        CdContrastBandRole.DarkAnchor => 0x1B1F24,
                        CdContrastBandRole.Reference => 0xCCD1D9,
                        _ => candidateColours[(band.Number - 1) % candidateColours.Length],
                    };
                }

                int offset = ((py * size) + px) * 4;
                pixels[offset] = (byte)colour;
                pixels[offset + 1] = (byte)(colour >> 8);
                pixels[offset + 2] = (byte)(colour >> 16);
                pixels[offset + 3] = 255;
            }
        }

        // Band numbers at 12 o'clock of each candidate band, and a key in the centre hole.
        foreach (CdContrastBand band in bands.Where(band => band.Role == CdContrastBandRole.Candidate))
        {
            double centre = (band.InnerRadiusMm + band.OuterRadiusMm) / 2;
            DrawText(pixels, size, band.Number.ToString(CultureInfo.InvariantCulture),
                size / 2, (int)Math.Round((size / 2.0) - (centre * scale)), 5, 0xFFFFFF);
        }

        int line = (size / 2) - (predictions.Count * 22 / 2) - 30;
        DrawText(pixels, size, "0D  AA", size / 2, line, 3, 0x1B1F24);
        foreach (CdContrastPrediction prediction in predictions)
        {
            line += 22;
            string change = (prediction.ChangeVersusStandard * 100).ToString("+0.0;-0.0", CultureInfo.InvariantCulture);
            DrawText(pixels, size, $"{prediction.Band} {prediction.Bytes} {change}%", size / 2, line, 3, 0x1B1F24);
        }

        RasterCodec.SavePng(path, size, size, pixels, grayscale: false);
    }

    private static void DrawText(byte[] pixels, int size, string text, int centreX, int centreY, int dot, uint colour)
    {
        int width = CdDotFont.Width(text) * dot;
        int left = centreX - (width / 2);
        int top = centreY - (CdDotFont.GlyphHeight * dot / 2);
        for (int y = 0; y < CdDotFont.GlyphHeight * dot; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!CdDotFont.Ink(text, x / dot, y / dot)) continue;
                int px = left + x, py = top + y;
                if ((uint)px >= size || (uint)py >= size) continue;
                int offset = ((py * size) + px) * 4;
                pixels[offset] = (byte)colour;
                pixels[offset + 1] = (byte)(colour >> 8);
                pixels[offset + 2] = (byte)(colour >> 16);
            }
        }
    }

    private sealed record LegendBand(int Number, string Role, string Bytes, double InnerRadiusMm, double OuterRadiusMm);

    private sealed record LegendDocument(
        string Format,
        string CandidateSet,
        string Dark,
        string Reference,
        double InnerRadiusMm,
        double OuterRadiusMm,
        long Sectors,
        bool Interleaved,
        IReadOnlyList<LegendBand> Bands,
        IReadOnlyList<CdContrastPrediction> Predictions,
        string HowToRead);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
