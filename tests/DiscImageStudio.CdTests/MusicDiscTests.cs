using System.Text;
using DiscImageStudio.Cd;
using DiscImageStudio.Cd.Music;
using DiscImageStudio.Core;

/// <summary>
/// Picture + music CDs: the sector layout, the disc stream (songs verbatim, the picture at exactly
/// the LBAs of a picture-only disc), the WAV/CUE/sidecar files and the command line.
/// </summary>
internal static class MusicDiscTests
{
    // A small disc keeps the tests fast: 4000 sectors (53 s), r 24.5–30 mm.
    private static readonly CdDiscParameters Disc = new(24.5, 30.0, 4000);
    private static readonly ToneProfile Tone = ToneProfile.Photo;

    internal static void Run()
    {
        string work = Fixtures.CreateWorkDirectory("music");
        try
        {
            CdMusicSource songA = WriteSong(Path.Combine(work, "a.wav"), 617_400, seed: 1, "Song A"); // 3.5 s
            CdMusicSource songB = WriteSong(Path.Combine(work, "b.wav"), 176_400, seed: 2, "Song B"); // 1 s
            TestLayout(songA, songB);
            TestStream(songA, songB, CdMusicPlacement.OuterRing);
            TestStream(songA, songB, CdMusicPlacement.InnerRing);
            TestFiles(work, songA, songB);
            TestPlaylistAndCommandLine(work);
            TestRequestRules(work, songA);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }

        Console.WriteLine("music-disc: passed (layout, picture LBAs equal the picture-only disc, songs verbatim, WAV/CUE/sidecar, playlist and CLI)");
    }

    private static void TestLayout(CdMusicSource songA, CdMusicSource songB)
    {
        // Outer ring, 2 s gap: A gets the gap (617400 + 352800 bytes = 412.5 → 413 sectors), the last
        // song does not (1 s is padded to the 4 s minimum: 300). Picture = 4000 - 75 - 713.
        CdMusicLayout outer = CdMusicLayout.Plan([songA, songB], Disc, CdMusicPlacement.OuterRing);
        Check.Equal(713L, outer.MusicSectors, "outer: padded song sectors");
        Check.Equal(0L, outer.PictureStartLba, "outer: the picture keeps LBA 0");
        Check.Equal(3212L, outer.PictureSectors, "outer: picture sectors");
        Check.Equal(3287L, outer.SpecialPregapSectors, "outer: picture + guard form the hidden pregap");
        Check.True(outer.TrackSectors.SequenceEqual([413L, 300L]), "outer: the songs are the tracks");
        Check.Equal(3287L, outer.Songs[0].StartLba, "outer: song 1 follows the guard");
        Check.Equal(3700L, outer.Songs[1].StartLba, "outer: song 2 follows song 1");
        Check.Equal(4000L * 2352, outer.StreamBytes, "outer: the stream fills the picture-only disc exactly");
        Check.Equal(outer.SpecialPregapSectors + outer.TrackSectors.Sum(), outer.StartOfLeadout, "outer: lead-out");
        Check.True(outer.PictureOuterRadiusMm < Disc.OutputOuterRadiusMm && outer.MusicInnerRadiusMm == outer.PictureOuterRadiusMm,
            "outer: the music ring starts where the picture ends");

        // Inner ring: every song gets the gap, the picture is the final track.
        CdMusicLayout inner = CdMusicLayout.Plan([songA, songB], Disc, CdMusicPlacement.InnerRing);
        Check.Equal(713L, inner.MusicSectors, "inner: padded song sectors (B: 1 s + 2 s gap = 225 → 300)");
        Check.Equal(713L, inner.PictureStartLba, "inner: the picture follows the songs");
        Check.Equal(3287L, inner.PictureSectors, "inner: the picture runs to the lead-out");
        Check.Equal(0L, inner.SpecialPregapSectors, "inner: no hidden pregap");
        Check.True(inner.TrackSectors.SequenceEqual([413L, 300L, 3287L]), "inner: the picture is the last track");
        Check.Equal(0L, inner.Songs[0].StartLba, "inner: song 1 at LBA 0");

        CdMusicLayout noGap = CdMusicLayout.Plan([songA, songB], Disc, CdMusicPlacement.OuterRing, gapSeconds: 0);
        Check.True(noGap.TrackSectors.SequenceEqual([300L, 300L]), "no gap: 3.5 s and 1 s are padded to 4 s");

        // Nine songs (8 x 413 + 300 + 75 guard = 3679) still leave 321 picture sectors; ten do not.
        Check.Equal(321L, CdMusicLayout.Plan(Enumerable.Repeat(songA, 9).ToArray(), Disc,
            CdMusicPlacement.OuterRing).PictureSectors, "nine songs leave just over 4 s for the picture");
        Check.Throws<CdMusicCapacityException>(() => CdMusicLayout.Plan(
            Enumerable.Repeat(songA, 10).ToArray(), Disc, CdMusicPlacement.OuterRing),
            "songs that leave under 4 s for the picture are refused");
        Check.Throws<CdMusicCapacityException>(() => CdMusicLayout.Plan(
            Enumerable.Repeat(songB, CdMusicLayout.MaximumSongs + 1).ToArray(), new CdDiscParameters(24.5, 58, 359_848),
            CdMusicPlacement.OuterRing), "more than 98 songs are refused");
        Check.Throws<ArgumentException>(() => CdMusicLayout.Plan([], Disc, CdMusicPlacement.OuterRing), "a music disc needs songs");
        Check.Throws<ArgumentOutOfRangeException>(() => CdMusicLayout.Plan([songA], Disc, CdMusicPlacement.OuterRing, 11),
            "gaps over 10 s are refused");
        Check.Equal("00:43:62", CdMusicLayout.Msf(3287), "MSF of 3287 sectors");
        Check.Equal("0:43", CdMusicLayout.Clock(3287), "clock of 3287 sectors");
    }

    private static void TestStream(CdMusicSource songA, CdMusicSource songB, CdMusicPlacement placement)
    {
        string name = placement == CdMusicPlacement.OuterRing ? "outer" : "inner";
        CdPalette palette = CdPalette.FromText(null, "F2-A8");
        CdMusicLayout layout = CdMusicLayout.Plan([songA, songB], Disc, placement);
        using MemoryStream stream = new();
        CdMusicDiscGenerator.GenerateToStream(Fixtures.IconImage, stream, layout, interleave: true, Tone, palette);
        byte[] disc = stream.ToArray();
        Check.Equal(layout.StreamBytes, (long)disc.Length, $"{name}: stream length");

        using MemoryStream plainStream = new();
        CdTrackGenerator.GenerateToStream(Fixtures.IconImage, plainStream, Disc, interleave: true,
            audioByteOrder: CdAudioByteOrder.LittleEndian, tone: Tone, palette: palette);
        byte[] plain = plainStream.ToArray();
        int pictureStart = checked((int)(layout.PictureStartLba * 2352));
        int pictureEnd = checked((int)(layout.PictureEndLba * 2352));
        if (placement == CdMusicPlacement.OuterRing)
        {
            Check.True(disc.AsSpan(0, pictureEnd).SequenceEqual(plain.AsSpan(0, pictureEnd)),
                "outer: the picture equals the picture-only disc at the same LBAs, byte for byte");
            Check.True(disc.AsSpan(pictureEnd, (int)CdMusicLayout.GuardSectors * 2352).IndexOfAnyExcept((byte)0) < 0,
                "outer: the guard before song 1 is silent");
        }
        else
        {
            // The interleaver starts empty at the picture's first byte: until its longest delay
            // has passed, a byte is either the picture-only disc's byte or silence.
            int span = CddaInterleaveTable.MaximumFileOffset;
            for (int offset = pictureStart; offset < pictureStart + span; offset++)
            {
                Check.True(disc[offset] == plain[offset] || disc[offset] == 0,
                    $"inner: byte {offset} in the first interleave span is the picture's or silence");
            }

            Check.True(disc.AsSpan(pictureStart + span, pictureEnd - pictureStart - span)
                    .SequenceEqual(plain.AsSpan(pictureStart + span, pictureEnd - pictureStart - span)),
                "inner: after one interleave span the picture equals the picture-only disc byte for byte");
        }

        foreach (CdMusicSong song in layout.Songs)
        {
            byte[] pcm = File.ReadAllBytes(song.Source.WavePath).AsSpan((int)song.Source.AudioOffset, (int)song.Source.AudioBytes).ToArray();
            int start = checked((int)song.StreamOffset);
            Check.True(disc.AsSpan(start, pcm.Length).SequenceEqual(pcm), $"{name}: {song.Source.Title} is copied verbatim");
            Check.True(disc.AsSpan(start + pcm.Length, (int)song.PaddingBytes).IndexOfAnyExcept((byte)0) < 0,
                $"{name}: {song.Source.Title} is padded with silence");
        }
    }

    private static void TestFiles(string work, CdMusicSource songA, CdMusicSource songB)
    {
        foreach (CdMusicPlacement placement in new[] { CdMusicPlacement.OuterRing, CdMusicPlacement.InnerRing })
        {
            string name = placement == CdMusicPlacement.OuterRing ? "outer" : "inner";
            string output = Path.Combine(work, $"disc-{name}.wav");
            DiscImageResult result = CdImageGenerator.Generate(new CdImageRequest(Fixtures.IconImage, output, Disc, Tone,
                Music: new CdMusicRequest([songA, songB], placement)));
            Check.True(result.Summary?.Contains("picture + music", StringComparison.Ordinal) == true, $"{name}: summary");
            CdMusicLayout layout = CdMusicLayout.Plan([songA, songB], Disc, placement);
            using (FileStream wave = File.OpenRead(output))
            {
                (long offset, long length) = CdWaveFile.ReadAudioRange(wave);
                Check.Equal(layout.StreamBytes, length, $"{name}: WAV audio length");
                using MemoryStream expected = new();
                CdMusicDiscGenerator.GenerateToStream(Fixtures.IconImage, expected, layout, interleave: true, Tone);
                byte[] data = new byte[length];
                wave.Position = offset;
                wave.ReadExactly(data);
                Check.True(data.AsSpan().SequenceEqual(expected.ToArray()), $"{name}: the WAV holds the disc stream");
            }

            string cue = File.ReadAllText(Path.ChangeExtension(output, ".cue"), Encoding.UTF8);
            string expectedCue = placement == CdMusicPlacement.OuterRing
                ? "REM COMMENT \"Disc Image Studio picture + music CD\"\r\n"
                    + "FILE \"disc-outer.wav\" WAVE\r\n"
                    + "  TRACK 01 AUDIO\r\n    TITLE \"Song A\"\r\n    INDEX 00 00:00:00\r\n    INDEX 01 00:43:62\r\n"
                    + "  TRACK 02 AUDIO\r\n    TITLE \"Song B\"\r\n    INDEX 01 00:49:25\r\n"
                : "REM COMMENT \"Disc Image Studio picture + music CD\"\r\n"
                    + "FILE \"disc-inner.wav\" WAVE\r\n"
                    + "  TRACK 01 AUDIO\r\n    TITLE \"Song A\"\r\n    INDEX 01 00:00:00\r\n"
                    + "  TRACK 02 AUDIO\r\n    TITLE \"Song B\"\r\n    INDEX 01 00:05:38\r\n"
                    + "  TRACK 03 AUDIO\r\n    TITLE \"Picture - loud noise, do not play\"\r\n    INDEX 01 00:09:38\r\n";
            Check.Equal(expectedCue, cue, $"{name}: CUE sheet");

            CdTrackMetadata metadata = CdTrackMetadata.TryLoad(output)!;
            Check.Equal(layout.PictureStartLba, metadata.PictureStartLba ?? -1, $"{name}: sidecar picture start");
            Check.Equal(layout.PictureSectors, metadata.PictureSectors ?? -1, $"{name}: sidecar picture sectors");
            Check.Equal(placement == CdMusicPlacement.OuterRing ? "outer" : "inner", metadata.MusicPlacement, $"{name}: sidecar placement");
            Check.True(metadata.Interleaved && metadata.CreateParameters() == Disc, $"{name}: sidecar geometry");

            string preview = Path.Combine(work, $"preview-{name}.png");
            CdTrackGenerator.PreviewWarp(Fixtures.IconImage, preview, Disc, Disc, 256, 4, Tone,
                layout.PictureStartLba, layout.PictureSectors);
            Check.True(new FileInfo(preview).Length > 0, $"{name}: picture-range preview renders");
        }

        Check.Equal("A 'quoted' title", CdWaveFile.CueText("A \"quoted\"\ttitle".Replace('\t', ' ')), "CUE text replaces quotes");
        Check.Equal(80, CdWaveFile.CueText(new string('x', 120)).Length, "CUE titles are cut to 80 characters");
    }

    private static void TestPlaylistAndCommandLine(string work)
    {
        string songs = Path.Combine(work, "songs");
        Directory.CreateDirectory(songs);
        WriteSong(Path.Combine(songs, "one.wav"), 352_800, seed: 3, null);
        WriteSong(Path.Combine(songs, "two.wav"), 529_200, seed: 4, null);
        string playlist = Path.Combine(work, "list.m3u8");
        File.WriteAllText(playlist, "﻿#EXTM3U\n#EXTINF:2,First song\nsongs/one.wav\n\n# comment\nsongs/two.wav\n",
            new UTF8Encoding(false));
        IReadOnlyList<CdMusicSource> read = CdMusicPlaylist.Read(playlist);
        Check.Equal(2, read.Count, "playlist songs");
        Check.Equal("First song", read[0].Title, "EXTINF sets the title");
        Check.Equal("two", read[1].Title, "the file name is the default title");
        Check.Equal(529_200L, read[1].AudioBytes, "playlist PCM length");
        File.WriteAllText(Path.Combine(work, "empty.m3u8"), "#EXTM3U\n");
        Check.Throws<ArgumentException>(() => CdMusicPlaylist.Read(Path.Combine(work, "empty.m3u8")), "empty playlists are refused");
        Check.Throws<ArgumentException>(() => CdMusicPlaylist.Read(Path.Combine(work, "list.txt")), "other files are refused");

        string mono = Path.Combine(work, "mono.wav");
        File.WriteAllBytes(mono, MonoWave());
        Check.Throws<InvalidDataException>(() => CdMusicSource.FromWave(mono), "non-CD WAVs are refused with a hint");

        CdDiscModule module = new();
        string output = Path.Combine(work, "cli.wav");
        DiscJobResult result = module.ExecuteAsync(new DiscJobRequest("cd-generate",
            ["--input", Fixtures.IconImage, "--output", output, "--r0", "24.5", "--r1", "30", "--sectors", "4000",
             "--music", playlist, "--music-position", "inner", "--music-gap", "0"])).GetAwaiter().GetResult();
        Check.True(result.Summary?.Contains("2 songs", StringComparison.Ordinal) == true, "CLI reports the songs");
        string cue = File.ReadAllText(Path.ChangeExtension(output, ".cue"));
        Check.True(cue.Contains("TITLE \"First song\"", StringComparison.Ordinal)
            && cue.Contains("TRACK 03 AUDIO", StringComparison.Ordinal), "CLI CUE has both songs and the picture track");

        string warp = Path.Combine(work, "cli-warp.png");
        module.ExecuteAsync(new DiscJobRequest("cd-preview-warp",
            ["--input", Fixtures.IconImage, "--output", warp, "--r0", "24.5", "--r1", "30", "--sectors", "4000",
             "--actual-r0", "24.5", "--actual-r1", "30", "--size", "256", "--music", playlist])).GetAwaiter().GetResult();
        Check.True(File.Exists(warp), "CLI geometry preview with music");
    }

    private static void TestRequestRules(string work, CdMusicSource song)
    {
        string data = Path.Combine(work, "files");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "a.txt"), "x");
        Check.Throws<ArgumentException>(() => CdImageGenerator.Generate(new CdImageRequest(Fixtures.IconImage,
            Path.Combine(work, "both.iso"), Disc, Tone, DataDirectory: data, Music: new CdMusicRequest([song]))),
            "files and songs cannot share a disc");
        Check.Throws<ArgumentException>(() => CdImageGenerator.Generate(new CdImageRequest(Fixtures.IconImage,
            Path.Combine(work, "music.raw"), Disc, Tone, Music: new CdMusicRequest([song]))),
            "music discs are written as WAV + CUE");
    }

    /// <summary>A CD-format WAV whose samples are seeded noise (asymmetric bytes would show any swap).</summary>
    private static CdMusicSource WriteSong(string path, int audioBytes, int seed, string? title)
    {
        byte[] pcm = new byte[audioBytes];
        new Random(seed).NextBytes(pcm);
        using (FileStream file = File.Create(path))
        {
            CdWaveFile.WriteHeader(file, audioBytes);
            file.Write(pcm);
        }

        return CdMusicSource.FromWave(path, title);
    }

    private static byte[] MonoWave()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + 400);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(44100);
        writer.Write(88200);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(400);
        writer.Write(new byte[400]);
        writer.Flush();
        return stream.ToArray();
    }
}
