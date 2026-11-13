using DiscImageStudio.Cd.Music;
using DiscImageStudio.Core;

namespace DiscImageStudio.Cd;

/// <summary>Songs for a picture + music CD: CD-format WAVs, where they go, and the silence
/// between them.</summary>
public sealed record CdMusicRequest(
    IReadOnlyList<CdMusicSource> Songs,
    CdMusicPlacement Placement = CdMusicPlacement.OuterRing,
    int GapSeconds = CdMusicLayout.DefaultGapSeconds);

/// <summary>A CD audio or data-image export, shared by graphical and command-line hosts.</summary>
public sealed record CdImageRequest(
    string SourceImage,
    string OutputPath,
    CdDiscParameters Parameters,
    ToneProfile Tone,
    bool Interleave = true,
    bool WriteCue = true,
    string? DataDirectory = null,
    string VolumeLabel = "CD_IMAGE",
    bool FilesAtOuterRing = false,
    CdPalette? Palette = null,
    CdMusicRequest? Music = null);

public static class CdImageGenerator
{
    /// <summary>Writes the image and recovery sidecars, preserving exceptions for the host.</summary>
    public static DiscImageResult Generate(CdImageRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        cancellationToken.ThrowIfCancellationRequested();
        request.Tone.Validate();
        CdPalette palette = request.Palette ?? CdPalette.Standard;
        palette.Validate(request.Tone.Levels);
        string? dataDirectory = request.DataDirectory?.Trim();
        if (request.Music is { } music)
        {
            if (!string.IsNullOrEmpty(dataDirectory))
            {
                throw new ArgumentException(
                    "A CD holds either on-disc files (a data disc) or songs (an audio disc), not both.", nameof(request));
            }

            CdMusicLayout layout = CdMusicLayout.Plan(music.Songs, request.Parameters, music.Placement, music.GapSeconds);
            CdGenerationSummary musicSummary = CdMusicDiscGenerator.Generate(request.SourceImage, request.OutputPath,
                layout, request.Interleave, request.Tone, palette,
                value => progress?.Report(new DiscJobProgress(CdDiscModule.ModuleId, "generate",
                    $"CD progress {value.Fraction:P1}", value.Fraction, value.CompletedBytes,
                    value.TotalBytes, value.Elapsed)), cancellationToken);
            return new DiscImageResult(musicSummary.OutputTrack,
                $"CD picture + music disc complete: {layout}, {layout.StreamSectors} sectors, "
                + (palette.IsStandard ? string.Empty : $"disc bytes={palette}, ")
                + $"output={musicSummary.OutputTrack}, cue={musicSummary.CueSheetPath}");
        }

        if (!string.IsNullOrEmpty(dataDirectory))
        {
            if (!palette.IsStandard)
            {
                throw new ArgumentException(
                    "Custom CD disc bytes apply to audio tracks only; data discs keep the standard 0D/AA bytes.",
                    nameof(request));
            }

            CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(dataDirectory, request.Parameters,
                request.VolumeLabel, request.FilesAtOuterRing);
            CdGenerationSummary dataSummary = CdDataImageGenerator.Generate(request.SourceImage,
                request.OutputPath, request.Parameters, plan,
                value => progress?.Report(new DiscJobProgress(CdDiscModule.ModuleId, "generate",
                    $"CD data progress {value.Fraction:P1}", value.Fraction, value.CompletedBytes,
                    value.TotalBytes, value.Elapsed)), cancellationToken, request.Tone);
            return new DiscImageResult(dataSummary.OutputTrack,
                $"CD data image complete: {plan.FileCount} files, {plan.TotalSectors} sectors, "
                + $"files={(request.FilesAtOuterRing ? "outer" : "inner")}, output={dataSummary.OutputTrack}");
        }

        CdGenerationSummary summary = CdTrackGenerator.Generate(request.SourceImage, request.OutputPath,
            request.Parameters, request.Interleave, request.WriteCue,
            value => progress?.Report(new DiscJobProgress(CdDiscModule.ModuleId, "generate",
                $"CD progress {value.Fraction:P1}", value.Fraction, value.CompletedBytes,
                value.TotalBytes, value.Elapsed)), cancellationToken, request.Tone, palette: palette);
        string message =
            $"CD track complete: {summary.Sectors} sectors, {summary.BytesWritten} bytes, "
            + $"interleave={summary.Interleaved}, elapsed={summary.Elapsed.TotalSeconds:F1}s, "
            + (palette.IsStandard ? string.Empty : $"disc bytes={palette}, ")
            + $"output={summary.OutputTrack}"
            + (summary.CueSheetPath is null ? string.Empty : $", cue={summary.CueSheetPath}");
        return new DiscImageResult(summary.OutputTrack, message);
    }
}
