using DiscImageStudio.Cd;
using DiscImageStudio.Core;

/// <summary>
/// Pins the exact bytes the CD generators produced before the contrast-tools changes, so the
/// default palette, tone mapping, interleave, byte order and preview paths stay byte-identical.
/// The hashes were recorded from the unmodified upstream sources (619ec75) with
/// <c>--print-golden</c>; regenerate them only for an intentional output change.
/// ISO images are pinned by their drawing sectors only, because the filesystem carries timestamps.
/// </summary>
internal static class GoldenOutputTests
{
    private static readonly CdDiscParameters Track = new(24.5, 56.8, 300);
    private static readonly CdDiscParameters DataDisc = new(24.5, 56.8, 400);

    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["track-legacy-interleave-be"] = "B5D9D49D2ADB039E544D41023DDEB178CAFE0FF730911A63C3BEC3943958A26E",
        ["track-legacy-interleave-le"] = "E93AF8695F9B2A693BFF0890D30BE4A9D0C8B06572E3B80BF99B60A123579655",
        ["track-legacy-plain-be"] = "DAE53D27B21852C6A8CD46E1CABAC8C5912425518EB2A2C05E392FAED33C1D79",
        ["track-photo-interleave-le"] = "E5493C0182018607B6B82EAA4DCDD2EF46D314D2774A1B89BCFF6F88FB4F1952",
        ["track-photo-serial-scalar"] = "E5493C0182018607B6B82EAA4DCDD2EF46D314D2774A1B89BCFF6F88FB4F1952",
        ["track-four-level-dither50"] = "547AE0289C17CBD8F0E20A50CC78FEEF55903964EFA8145A1C0891B7F4DCD5A2",
        ["track-gamma-brightness"] = "666C5257F1A39DE6D69AE5E1CCB932DAC03168BD3C6AC8A33C54934C443001BD",
        ["pattern-checker-le"] = "831D883DDD1D039DFEE13610E49B542DCD37FFB17FF8636EF052AEBEB0419E6C",
        ["file-photo-wav"] = "20A27F3278D9DBA91FE53A5760317A842A3CFBC4375D88A8D4929C8B813A531D",
        ["preview-track-png"] = "DADC99085706CC42C45FA71CA49AF0B6AA8776E3752328B0B3D1F1BBDD990001",
        ["preview-warp-four-level-png"] = "597C0C5572AF21F6E4F9230C3B1B3C7D9C0E5AD0FA8183189F408CABE862B90D",
        ["data-iso-inner-drawing"] = "4A689F5E85D0293972C9CCB24896A8F9613D13C59DA0E71DF3E6D1B407B3835E",
        ["data-iso-outer-drawing"] = "3E5036A82137CB82253C5E9B88476F146153F25834FB5F6389533A17A00168EF",
    };

    internal static void Run()
    {
        Dictionary<string, string> actual = Compute();
        foreach ((string name, string hash) in actual)
        {
            if (!Expected.TryGetValue(name, out string? expected))
            {
                throw new InvalidOperationException($"golden output {name}: no recorded hash (run with --print-golden).");
            }

            Check.Equal(expected, hash, $"golden output {name}");
        }

        Check.Equal(Expected.Count, actual.Count, "golden output case count");
        Console.WriteLine($"golden outputs: passed ({actual.Count} cases)");
    }

    internal static void Print()
    {
        foreach ((string name, string hash) in Compute())
        {
            Console.WriteLine($"        [\"{name}\"] = \"{hash}\",");
        }
    }

    private static Dictionary<string, string> Compute()
    {
        string work = Fixtures.CreateWorkDirectory("golden");
        try
        {
            Dictionary<string, string> hashes = new(StringComparer.Ordinal);
            string image = Fixtures.IconImage;

            void TrackCase(string name, bool interleave, CdAudioByteOrder order, ToneProfile tone,
                bool parallel = true, bool simd = true)
            {
                using MemoryStream output = new();
                CdTrackGenerator.GenerateToStream(image, output, Track, interleave, audioByteOrder: order,
                    tone: tone, useSimdSampling: simd, useParallelSampling: parallel);
                hashes[name] = Check.Sha256(output.ToArray());
            }

            TrackCase("track-legacy-interleave-be", true, CdAudioByteOrder.BigEndian, ToneProfile.LegacyBinary);
            TrackCase("track-legacy-interleave-le", true, CdAudioByteOrder.LittleEndian, ToneProfile.LegacyBinary);
            TrackCase("track-legacy-plain-be", false, CdAudioByteOrder.BigEndian, ToneProfile.LegacyBinary);
            TrackCase("track-photo-interleave-le", true, CdAudioByteOrder.LittleEndian, ToneProfile.Photo);
            TrackCase("track-photo-serial-scalar", true, CdAudioByteOrder.LittleEndian, ToneProfile.Photo,
                parallel: false, simd: false);
            TrackCase("track-four-level-dither50", true, CdAudioByteOrder.BigEndian, new ToneProfile(0.5, 4, 0));
            TrackCase("track-gamma-brightness", true, CdAudioByteOrder.BigEndian, new ToneProfile(0.3, 2, 20, true));

            using (MemoryStream pattern = new())
            {
                CdTrackGenerator.GeneratePatternToStream(
                    (x, y) => ((int)Math.Floor(x / 3.0) + (int)Math.Floor(y / 3.0)) % 2 == 0 ? (byte)0 : (byte)255,
                    pattern, Track, interleave: true, audioByteOrder: CdAudioByteOrder.LittleEndian);
                hashes["pattern-checker-le"] = Check.Sha256(pattern.ToArray());
            }

            string wave = Path.Combine(work, "golden.wav");
            CdTrackGenerator.Generate(image, wave, Track, interleave: true, tone: ToneProfile.Photo);
            hashes["file-photo-wav"] = Check.Sha256(File.ReadAllBytes(wave));

            string readback = Path.Combine(work, "readback.png");
            CdTrackGenerator.PreviewTrack(wave, readback, Track, 256, 7, deinterleave: true);
            hashes["preview-track-png"] = Check.Sha256(File.ReadAllBytes(readback));

            string warp = Path.Combine(work, "warp.png");
            CdTrackGenerator.PreviewWarp(image, warp, Track, Track, 256, 64, new ToneProfile(0.5, 4, 0));
            hashes["preview-warp-four-level-png"] = Check.Sha256(File.ReadAllBytes(warp));

            string data = Path.Combine(work, "data");
            Directory.CreateDirectory(data);
            File.WriteAllBytes(Path.Combine(data, "a.bin"), Enumerable.Range(0, 70_000).Select(i => (byte)(i * 7)).ToArray());
            File.WriteAllText(Path.Combine(data, "readme.txt"), "contrast tools golden fixture\n");
            foreach (bool outer in new[] { false, true })
            {
                CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(data, DataDisc, "GOLDEN", outer);
                using MemoryStream iso = new();
                CdDataImageGenerator.GenerateToStream(image, iso, DataDisc, plan, tone: ToneProfile.Photo);
                byte[] bytes = iso.ToArray();
                int start = checked((int)(plan.DrawingStartLba * CdDataImageGenerator.BytesPerSector));
                int end = checked((int)(plan.DrawingEndLbaExclusive * CdDataImageGenerator.BytesPerSector));
                hashes[outer ? "data-iso-outer-drawing" : "data-iso-inner-drawing"] = Check.Sha256(bytes.AsSpan(start, end - start));
            }

            return hashes;
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
