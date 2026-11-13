using System.Text.Json;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;

/// <summary>Custom two-level disc bytes: the standard path is unchanged, custom bytes are applied
/// consistently, previews read them back and unsafe or unsupported combinations are refused.</summary>
internal static class PaletteTests
{
    private static readonly CdDiscParameters Track = new(24.5, 56.8, 120);

    internal static void Run()
    {
        TestStandardEquivalence();
        TestPairDecisions();
        TestSingleByteSubstitution();
        TestPreviewAndSidecar();
        TestValidation();
        TestCommandLineSafetyGate();
        Console.WriteLine("palette: passed");
    }

    /// <summary>MapLevel + the standard palette reproduce Map for every grey, tone and phase.</summary>
    private static void TestStandardEquivalence()
    {
        ToneProfile[] tones =
        [
            ToneProfile.LegacyBinary, ToneProfile.Photo, new(0.5, 4, 0), new(0.3, 2, 20, true),
            new(1, 2, -10), new(0.75, 4, 12, true),
        ];
        foreach (ToneProfile tone in tones)
        {
            for (int gray = 0; gray < 256; gray++)
            {
                for (long index = 0; index < 97; index++)
                {
                    int level = CdToneMapping.MapLevel((byte)gray, tone, index);
                    byte expected = CdToneMapping.Map((byte)gray, tone, index);
                    Check.Equal(expected, CdToneMapping.Palette(tone.Levels)[level], $"MapLevel {tone} grey {gray} index {index}");
                    if (tone.Levels == ToneProfile.TwoLevels)
                    {
                        Check.Equal(expected, CdPalette.Standard.Encode(level, index), "standard palette encoding");
                    }
                }
            }
        }
    }

    /// <summary>With a byte pair, both bytes of an even/odd pair always carry the same tone.</summary>
    private static void TestPairDecisions()
    {
        CdPalette palette = CdPalette.FromText("0D", "F2-A8");
        CdPaletteEncoder encoder = new(palette, ToneProfile.Photo);
        Random random = new(5);
        for (long index = 0; index < 20_000; index += 2)
        {
            byte even = encoder.Encode((byte)random.Next(256), index);
            byte odd = encoder.Encode((byte)random.Next(256), index + 1);
            bool light = even == 0xF2;
            Check.True(light ? odd == 0xA8 : even == 0x0D && odd == 0x0D, $"pair at {index} is not split ({even:X2} {odd:X2})");
        }
    }

    /// <summary>A single-byte custom light tone changes exactly the light bytes of the track.</summary>
    private static void TestSingleByteSubstitution()
    {
        CdPalette palette = CdPalette.FromText(null, "B6");
        foreach (bool parallel in new[] { true, false })
        {
            using MemoryStream standard = new();
            using MemoryStream custom = new();
            CdTrackGenerator.GenerateToStream(Fixtures.IconImage, standard, Track, interleave: true,
                tone: ToneProfile.Photo, useParallelSampling: parallel);
            CdTrackGenerator.GenerateToStream(Fixtures.IconImage, custom, Track, interleave: true,
                tone: ToneProfile.Photo, useParallelSampling: parallel, palette: palette);
            byte[] expected = standard.ToArray();
            for (int index = 0; index < expected.Length; index++)
            {
                if (expected[index] == 0xAA) expected[index] = 0xB6;
            }

            Check.True(expected.AsSpan().SequenceEqual(custom.ToArray()), $"B6 replaces AA one for one (parallel {parallel})");
        }
    }

    private static void TestPreviewAndSidecar()
    {
        string work = Fixtures.CreateWorkDirectory("palette");
        try
        {
            foreach (string light in new[] { "B6", "F2-A8" })
            {
                CdPalette palette = CdPalette.FromText(null, light);
                string wave = Path.Combine(work, $"custom-{light}.wav");
                CdTrackGenerator.Generate(Fixtures.IconImage, wave, Track, interleave: true,
                    tone: ToneProfile.LegacyBinary, palette: palette);
                CdTrackMetadata metadata = CdTrackMetadata.TryLoad(wave)
                    ?? throw new InvalidOperationException("sidecar missing");
                Check.Equal(palette, metadata.CreatePalette(), $"sidecar palette {light}");

                string readback = Path.Combine(work, $"readback-{light}.png");
                string warp = Path.Combine(work, $"warp-{light}.png");
                CdTrackGenerator.PreviewTrack(wave, readback, Track, 256, 1, deinterleave: true, palette: metadata.CreatePalette());
                CdTrackGenerator.PreviewWarp(Fixtures.IconImage, warp, Track, Track, 256, CdDiscParameters.BytesPerSector,
                    ToneProfile.LegacyBinary);
                // Hard-threshold tone: a pair palette decides each pair on its even byte, so only
                // pixel edges can differ from the per-byte projection.
                double difference = MeanAbsoluteDifference(readback, warp);
                Check.True(palette.DecidesPerPair ? difference < 2.0 : difference == 0,
                    $"read-back of {light} matches the projection (mean difference {difference:F3})");
            }

            string standardWave = Path.Combine(work, "standard.wav");
            CdTrackGenerator.Generate(Fixtures.IconImage, standardWave, Track, interleave: true);
            using JsonDocument sidecar = JsonDocument.Parse(File.ReadAllText(standardWave + ".json"));
            Check.True(!sidecar.RootElement.TryGetProperty("paletteDark", out _)
                && !sidecar.RootElement.TryGetProperty("paletteLight", out _), "standard sidecars carry no palette fields");
            Check.Equal(CdPalette.Standard, CdTrackMetadata.TryLoad(standardWave)!.CreatePalette(), "absent palette is standard");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static double MeanAbsoluteDifference(string first, string second)
    {
        DiscImageStudio.ImageIO.BgraRaster left = DiscImageStudio.ImageIO.RasterCodec.LoadBgra32(first);
        DiscImageStudio.ImageIO.BgraRaster right = DiscImageStudio.ImageIO.RasterCodec.LoadBgra32(second);
        Check.True(left.Width == right.Width && left.Height == right.Height, "preview sizes match");
        byte[] a = left.Pixels, b = right.Pixels;
        long sum = 0;
        for (int index = 0; index < a.Length; index += 4) sum += Math.Abs(a[index] - b[index]);
        return (double)sum / (a.Length / 4);
    }

    private static void TestValidation()
    {
        Check.Throws<ArgumentException>(() => CdPalette.FromText(null, "F2-A8").Validate(ToneProfile.FourLevels),
            "custom bytes need two tone levels");
        Check.Throws<ArgumentException>(() => CdPalette.FromText("0D-15", "0D-AA").Validate(ToneProfile.TwoLevels),
            "dark and light must differ at both parities");
        CdPalette.Standard.Validate(ToneProfile.FourLevels);
        Check.Throws<FormatException>(() => CdBytePattern.Parse("XYZ"), "invalid hex is refused");
        Check.Equal(new CdBytePattern(0xF2, 0xA8), CdBytePattern.Parse("f2-a8"), "pattern parse");
        Check.Equal("F2-A8", new CdBytePattern(0xF2, 0xA8).ToString(), "pattern text");

        string work = Fixtures.CreateWorkDirectory("palette-data");
        try
        {
            string data = Path.Combine(work, "files");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "a.txt"), "x");
            CdImageRequest request = new(Fixtures.IconImage, Path.Combine(work, "data.iso"), new CdDiscParameters(24.5, 56.8, 400),
                ToneProfile.LegacyBinary, DataDirectory: data, Palette: CdPalette.FromText(null, "B6"));
            Check.Throws<ArgumentException>(() => CdImageGenerator.Generate(request), "data discs keep the standard bytes");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>cd-generate refuses bytes outside the shipped envelope unless explicitly allowed.</summary>
    private static void TestCommandLineSafetyGate()
    {
        string work = Fixtures.CreateWorkDirectory("palette-cli");
        try
        {
            CdDiscModule module = new();
            string[] baseArguments = ["--input", Fixtures.IconImage, "--sectors", "40"];
            DiscJobResult Run(string output, params string[] extra)
                => module.ExecuteAsync(new DiscJobRequest("cd-generate",
                    [.. baseArguments, "--output", Path.Combine(work, output), .. extra])).GetAwaiter().GetResult();

            Check.Throws<ArgumentException>(() => Run("unvetted.wav", "--palette-light", "F7-A8"),
                "F7-A8 with 0D fails the envelope");
            Run("allowed.wav", "--palette-light", "F7-A8", "--allow-unvetted", "true");
            Run("vetted.wav", "--palette-light", "F2-A8");
            Check.Equal("F2-A8", CdTrackMetadata.TryLoad(Path.Combine(work, "vetted.wav"))!.PaletteLight, "CLI sidecar palette");
            Run("standard.wav", "--palette-light", "AA");
            Check.True(CdTrackMetadata.TryLoad(Path.Combine(work, "standard.wav"))!.PaletteLight is null,
                "explicit standard bytes are the standard palette");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
