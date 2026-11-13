using System.IO;

namespace DiscImageStudio.Cd.Music;

/// <summary>Where the songs of a picture + music disc go.</summary>
public enum CdMusicPlacement
{
    /// <summary>Songs on the outer ring. The picture fills the middle of the disc inside track 1's
    /// pregap (index 00), which CD players skip: they start at song 1.</summary>
    OuterRing,

    /// <summary>Songs on the inner ring. The picture is the last track, a ring around them; it
    /// plays as loud noise if a player keeps going after the music.</summary>
    InnerRing,
}

/// <summary>One song: the PCM range of a CD-format WAV (44.1 kHz, 16-bit, stereo) and a title.</summary>
public sealed record CdMusicSource(string WavePath, string Title, long AudioOffset, long AudioBytes)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(AudioBytes / (double)CdMusicLayout.BytesPerSecond);

    /// <summary>Reads the song's PCM range from a CD-format WAV; other formats are refused with a
    /// hint to convert them first (the app converts them itself).</summary>
    public static CdMusicSource FromWave(string wavePath, string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavePath);
        string fullPath = Path.GetFullPath(wavePath);
        using FileStream input = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        (long offset, long length) range;
        try
        {
            range = CdWaveFile.ReadAudioRange(input);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(fullPath)}: {exception.Message} Convert it first, for example "
                + "\"ffmpeg -i song.mp3 -ar 44100 -ac 2 -sample_fmt s16 song.wav\".", exception);
        }

        return new(fullPath, string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fullPath) : title.Trim(),
            range.offset, range.length);
    }
}

/// <summary>A planned song: its track number, padded length and where it starts on the disc. The
/// disc stream (the exported WAV's audio data, and the burned content) starts at LBA 0, so a song's
/// stream offset is its LBA × 2352.</summary>
public sealed record CdMusicSong(CdMusicSource Source, int TrackNumber, long Sectors, long StartLba, long StreamOffset)
{
    public long PaddingBytes => Sectors * CdDiscParameters.BytesPerSector - Source.AudioBytes;
}

/// <summary>The songs do not fit beside the picture; the message is ready to show.</summary>
public sealed class CdMusicCapacityException(string message) : ArgumentException(message);

/// <summary>
/// Sector layout of a picture + music CD. The picture keeps exactly the LBAs it has on a
/// picture-only disc (global byte = LBA × 2352): one sector of offset would twist it by about 20°
/// across the disc. Songs are padded with silence to whole sectors and to the 4-second minimum.
/// The disc stream always starts at LBA 0, after the standard silent 2-second pregap that the
/// recorder (IMAPI) or a CUE burner adds itself.
/// <list type="bullet">
/// <item>Outer-ring music: the picture at LBA 0…B-1, then a 75-sector silent guard; together they
/// continue track 1's pregap (index 00), which players skip ("hidden track one audio"). Song 1's
/// index 01 follows at LBA B + 75.</item>
/// <item>Inner-ring music: songs from LBA 0, then the picture as the final track up to the lead-out.</item>
/// </list>
/// Both fill exactly the sectors of a picture-only disc, so its capacity checks still apply.
/// </summary>
public sealed class CdMusicLayout
{
    public const int BytesPerSecond = 176_400;
    public const long GuardSectors = 75;
    public const long MinimumTrackSectors = 300;
    public const int MaximumSongs = 98;
    public const int MaximumGapSeconds = 10;
    public const int DefaultGapSeconds = 2;

    private CdMusicLayout(CdDiscParameters parameters, CdMusicPlacement placement, int gapSeconds,
        IReadOnlyList<CdMusicSong> songs, long musicSectors, long pictureStartLba, long pictureSectors)
    {
        Parameters = parameters;
        Placement = placement;
        GapSeconds = gapSeconds;
        Songs = songs;
        MusicSectors = musicSectors;
        PictureStartLba = pictureStartLba;
        PictureSectors = pictureSectors;
    }

    public CdDiscParameters Parameters { get; }
    public CdMusicPlacement Placement { get; }
    public int GapSeconds { get; }
    public IReadOnlyList<CdMusicSong> Songs { get; }

    /// <summary>Padded song sectors, gaps included.</summary>
    public long MusicSectors { get; }

    public long PictureStartLba { get; }
    public long PictureSectors { get; }
    public long PictureEndLba => PictureStartLba + PictureSectors;

    public long OutputSectors => Parameters.OutputSectors;
    public long StartOfLeadout => OutputSectors;

    /// <summary>The disc stream: LBA 0 up to the lead-out, the same length as a picture-only disc.</summary>
    public long StreamSectors => OutputSectors;
    public long StreamBytes => checked(StreamSectors * CdDiscParameters.BytesPerSector);

    /// <summary>Hidden part of track 1's index 00 at the start of the stream, for IMAPI's special
    /// pregap: the picture and the guard on the outer ring, none on the inner ring.</summary>
    public long SpecialPregapSectors => Placement == CdMusicPlacement.OuterRing ? PictureSectors + GuardSectors : 0;

    /// <summary>The IMAPI tracks in order: the songs, plus the picture as the last track on the inner ring.</summary>
    public IReadOnlyList<long> TrackSectors => Placement == CdMusicPlacement.OuterRing
        ? Songs.Select(song => song.Sectors).ToArray()
        : [.. Songs.Select(song => song.Sectors), PictureSectors];

    /// <summary>Where the songs (with the guard) sit, as an LBA range.</summary>
    public long MusicStartLba => Placement == CdMusicPlacement.OuterRing ? PictureEndLba : 0;
    public long MusicEndLba => Placement == CdMusicPlacement.OuterRing ? StartOfLeadout : PictureStartLba;

    public double PictureInnerRadiusMm => RadiusAt(PictureStartLba);
    public double PictureOuterRadiusMm => RadiusAt(PictureEndLba);
    public double MusicInnerRadiusMm => RadiusAt(MusicStartLba);
    public double MusicOuterRadiusMm => RadiusAt(MusicEndLba);

    /// <summary>Share of the written disc area the picture keeps (area grows linearly with time).</summary>
    public double PictureShare => PictureSectors / (double)OutputSectors;

    public TimeSpan MusicDuration => TimeSpan.FromSeconds(Songs.Sum(song => song.Source.AudioBytes) / (double)BytesPerSecond);

    private double RadiusAt(long lba) => Parameters.RadiusFromByte(lba * CdDiscParameters.BytesPerSector);

    public static CdMusicLayout Plan(IReadOnlyList<CdMusicSource> songs, CdDiscParameters parameters,
        CdMusicPlacement placement, int gapSeconds = DefaultGapSeconds)
    {
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        if (!Enum.IsDefined(placement))
        {
            throw new ArgumentOutOfRangeException(nameof(placement));
        }

        if (gapSeconds is < 0 or > MaximumGapSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(gapSeconds), $"The gap between songs must be 0–{MaximumGapSeconds} seconds.");
        }

        if (songs.Count == 0)
        {
            throw new ArgumentException("A music disc needs at least one song.", nameof(songs));
        }

        if (songs.Count > MaximumSongs)
        {
            throw new CdMusicCapacityException($"A CD holds at most {MaximumSongs} songs beside the picture; {songs.Count} were chosen.");
        }

        long gapBytes = (long)gapSeconds * BytesPerSecond;
        long[] sectors = new long[songs.Count];
        for (int index = 0; index < songs.Count; index++)
        {
            CdMusicSource song = songs[index];
            if (song.AudioBytes <= 0 || song.AudioBytes % 4 != 0)
            {
                throw new ArgumentException($"{song.Title}: the song has no complete stereo samples.", nameof(songs));
            }

            // On the inner ring the last song's gap also separates the music from the picture track.
            bool gapAfter = placement == CdMusicPlacement.InnerRing || index < songs.Count - 1;
            long bytes = checked(song.AudioBytes + (gapAfter ? gapBytes : 0));
            sectors[index] = Math.Max(MinimumTrackSectors,
                (bytes + CdDiscParameters.BytesPerSector - 1) / CdDiscParameters.BytesPerSector);
        }

        long musicSectors = sectors.Sum();
        long outputSectors = parameters.OutputSectors;
        long reserved = placement == CdMusicPlacement.OuterRing ? GuardSectors : 0;
        long pictureSectors = outputSectors - reserved - musicSectors;
        if (pictureSectors < MinimumTrackSectors)
        {
            throw new CdMusicCapacityException(
                $"The songs need {Clock(musicSectors + reserved)} of this disc's {Clock(outputSectors)}; "
                + "at least 4 seconds must stay free for the picture.");
        }

        long pictureStartLba = placement == CdMusicPlacement.OuterRing ? 0 : musicSectors;
        long songLba = placement == CdMusicPlacement.OuterRing ? pictureSectors + GuardSectors : 0;
        List<CdMusicSong> planned = new(songs.Count);
        for (int index = 0; index < songs.Count; index++)
        {
            planned.Add(new CdMusicSong(songs[index], index + 1, sectors[index], songLba,
                songLba * CdDiscParameters.BytesPerSector));
            songLba += sectors[index];
        }

        return new CdMusicLayout(parameters, placement, gapSeconds, planned, musicSectors, pictureStartLba, pictureSectors);
    }

    /// <summary>Playing time of a sector count as m:ss (75 sectors per second).</summary>
    public static string Clock(long sectors)
    {
        long seconds = sectors / 75;
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>CUE/MSF time of a sector count: mm:ss:ff.</summary>
    internal static string Msf(long sectors)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sectors);
        return $"{sectors / 4500:00}:{sectors / 75 % 60:00}:{sectors % 75:00}";
    }

    public override string ToString()
        => $"{Songs.Count} songs ({Clock(MusicSectors)}) on the {(Placement == CdMusicPlacement.OuterRing ? "outer" : "inner")} ring; "
            + $"picture LBA {PictureStartLba}–{PictureEndLba - 1}, {PictureInnerRadiusMm:0.0}–{PictureOuterRadiusMm:0.0} mm";
}

/// <summary>Reads the songs a command line names: one CD-format WAV, or an M3U/M3U8 playlist
/// (one path per line, relative to the playlist; "#EXTINF:seconds,Title" sets the next title).</summary>
public static class CdMusicPlaylist
{
    public static IReadOnlyList<CdMusicSource> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string extension = Path.GetExtension(fullPath);
        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return [CdMusicSource.FromWave(fullPath)];
        }

        if (!extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".m3u", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--music must be a CD-format .wav file or an .m3u8/.m3u playlist of them.", nameof(path));
        }

        string directory = Path.GetDirectoryName(fullPath)!;
        List<CdMusicSource> songs = [];
        string? title = null;
        foreach (string rawLine in File.ReadAllLines(fullPath))
        {
            string line = rawLine.Trim().TrimStart('﻿');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase) && line.IndexOf(',') is int comma and > 0)
                {
                    title = line[(comma + 1)..].Trim();
                }

                continue;
            }

            songs.Add(CdMusicSource.FromWave(Path.IsPathRooted(line) ? line : Path.Combine(directory, line), title));
            title = null;
        }

        if (songs.Count == 0)
        {
            throw new ArgumentException($"The playlist {Path.GetFileName(fullPath)} names no songs.", nameof(path));
        }

        return songs;
    }
}
