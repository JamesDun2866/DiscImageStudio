using System.Diagnostics;
using System.IO;
using System.Text;
using DiscImageStudio.Core;

namespace DiscImageStudio.Cd.Music;

/// <summary>
/// Writes a picture + music CD: the songs verbatim (no interleave, no tone mapping), padded with
/// silence to their planned sectors, and the picture through the normal generator but only for its
/// own LBA range, so its bytes keep the positions they have on a picture-only disc. The stream is
/// little-endian CD audio from LBA 0 to the lead-out: the exported WAV's audio data and the burned
/// content are the same bytes.
/// </summary>
public static class CdMusicDiscGenerator
{
    public const string PictureTrackTitle = "Picture - loud noise, do not play";

    public static CdGenerationSummary GenerateToStream(
        string imagePath,
        Stream output,
        CdMusicLayout layout,
        bool interleave,
        ToneProfile? tone = null,
        CdPalette? palette = null,
        Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(layout);
        if (!output.CanWrite)
        {
            throw new ArgumentException("CD output stream must be writable.", nameof(output));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch stopwatch = Stopwatch.StartNew();
        long total = layout.StreamBytes;
        long written = 0;
        void Report(long segmentBytes) => progress?.Invoke(new CdProgress(written + segmentBytes, total, stopwatch.Elapsed));

        void Picture()
        {
            CdTrackGenerator.GeneratePictureRangeToStream(imagePath, output, layout.Parameters, interleave,
                layout.PictureStartLba, layout.PictureSectors, CdAudioByteOrder.LittleEndian, tone, palette,
                value => Report(value.CompletedBytes), cancellationToken);
            written += layout.PictureSectors * CdDiscParameters.BytesPerSector;
        }

        void Songs()
        {
            foreach (CdMusicSong song in layout.Songs)
            {
                CopySong(song, output, Report, cancellationToken);
                written += song.Sectors * CdDiscParameters.BytesPerSector;
            }
        }

        if (layout.Placement == CdMusicPlacement.OuterRing)
        {
            Picture();
            WriteSilence(output, CdMusicLayout.GuardSectors * CdDiscParameters.BytesPerSector, cancellationToken);
            written += CdMusicLayout.GuardSectors * CdDiscParameters.BytesPerSector;
            Songs();
        }
        else
        {
            Songs();
            Picture();
        }

        if (written != total)
        {
            throw new InvalidOperationException($"The music disc stream has {written} bytes; its layout needs {total}.");
        }

        output.Flush();
        stopwatch.Stop();
        progress?.Invoke(new CdProgress(total, total, stopwatch.Elapsed));
        return new CdGenerationSummary(Path.GetFullPath(imagePath), "music-disc-stream", layout.StreamSectors, total,
            interleave, stopwatch.Elapsed);
    }

    /// <summary>Writes the disc as one WAV (the whole stream), a CUE sheet with one track per song,
    /// and the sidecar the read-back preview uses.</summary>
    public static CdGenerationSummary Generate(
        string imagePath,
        string outputPath,
        CdMusicLayout layout,
        bool interleave,
        ToneProfile? tone = null,
        CdPalette? palette = null,
        Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        string sourceFullPath = Path.GetFullPath(imagePath);
        string outputFullPath = Path.GetFullPath(outputPath);
        if (!Path.GetExtension(outputFullPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A picture + music CD is written as a .wav file with a CUE sheet.", nameof(outputPath));
        }

        if (sourceFullPath.Equals(outputFullPath, StringComparison.OrdinalIgnoreCase)
            || layout.Songs.Any(song => song.Source.WavePath.Equals(outputFullPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The output must not overwrite the source image or a song.", nameof(outputPath));
        }

        ToneProfile effectiveTone = (tone ?? ToneProfile.LegacyBinary).Normalized();
        CdPalette effectivePalette = palette ?? CdPalette.Standard;
        effectivePalette.Validate(effectiveTone.Levels);
        CdWaveFile.ValidateLength(layout.StreamBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);
        CdGenerationSummary summary;
        using (FileStream file = new(outputFullPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1024 * 1024, FileOptions.SequentialScan))
        {
            CdWaveFile.WriteHeader(file, layout.StreamBytes);
            summary = GenerateToStream(sourceFullPath, file, layout, interleave, effectiveTone, effectivePalette,
                progress, cancellationToken);
        }

        CdDiscParameters parameters = layout.Parameters;
        CdTrackMetadata.Save(outputFullPath, new CdTrackMetadata(
            sourceFullPath,
            outputFullPath,
            parameters.InnerRadiusMm,
            parameters.OuterRadiusMm,
            parameters.Sectors,
            parameters.LinearVelocityMmPerSecond,
            parameters.StartAngleRadians * 180.0 / Math.PI,
            parameters.ImageOuterRadiusMm,
            interleave,
            effectiveTone.DitherStrength,
            effectiveTone.Levels,
            effectiveTone.Brightness,
            effectiveTone.SrgbLinearization,
            parameters.PitchLinear,
            parameters.PitchQuadratic,
            parameters.PitchCubic,
            parameters.UsableSectors,
            parameters.SourceImageOuterRadiusMm,
            effectivePalette.IsStandard ? null : effectivePalette.Dark.ToString(),
            effectivePalette.IsStandard ? null : effectivePalette.Light.ToString(),
            PictureStartLba: layout.PictureStartLba,
            PictureSectors: layout.PictureSectors,
            MusicPlacement: layout.Placement == CdMusicPlacement.OuterRing ? "outer" : "inner"));
        return summary with
        {
            OutputTrack = outputFullPath,
            CueSheetPath = CdWaveFile.WriteMusicCue(outputFullPath, layout),
        };
    }

    private static void CopySong(CdMusicSong song, Stream output, Action<long> report, CancellationToken cancellationToken)
    {
        using FileStream input = new(song.Source.WavePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        // The disc was planned from this file's PCM range; a file changed since then would move every
        // later track and the picture with it.
        (long offset, long length) = CdWaveFile.ReadAudioRange(input);
        if (offset != song.Source.AudioOffset || length != song.Source.AudioBytes)
        {
            throw new InvalidDataException($"{Path.GetFileName(song.Source.WavePath)} changed after the disc was planned; plan it again.");
        }

        input.Position = offset;
        byte[] buffer = new byte[1024 * 1024];
        long copied = 0;
        while (copied < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length - copied));
            if (count <= 0)
            {
                throw new EndOfStreamException($"{Path.GetFileName(song.Source.WavePath)} ended early.");
            }

            output.Write(buffer, 0, count);
            copied += count;
            report(copied);
        }

        WriteSilence(output, song.PaddingBytes, cancellationToken);
    }

    private static void WriteSilence(Stream output, long bytes, CancellationToken cancellationToken)
    {
        byte[] zeros = new byte[CdDiscParameters.BytesPerSector * 32];
        while (bytes > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(zeros.Length, bytes);
            output.Write(zeros, 0, count);
            bytes -= count;
        }
    }
}
