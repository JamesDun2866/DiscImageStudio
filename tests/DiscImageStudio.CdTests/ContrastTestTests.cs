using System.Text.Json;
using DiscImageStudio.Cd;
using DiscImageStudio.Cd.Contrast;
using DiscImageStudio.Cd.Physical;

/// <summary>The light-side contrast test disc: layout, exact bytes, physical uniformity of the
/// candidate bands (parity included), candidate safety and the written legend.</summary>
internal static class ContrastTestTests
{
    private static readonly CdDiscParameters Verbatim43438 = new(24.837775, 58.020875, 359848, UsableSectors: 359846);

    internal static void Run()
    {
        TestLayout();
        TestSamplerBytes();
        TestCandidateBandsAreUniformOnDisc();
        TestCandidatesAreSafeAndBrighter();
        TestGeneratedFiles();
        Console.WriteLine("contrast test: passed");
    }

    private static void TestLayout()
    {
        foreach (CdDiscParameters parameters in new[] { Verbatim43438, new CdDiscParameters(24.5, 56.8, 359849) })
        {
            IReadOnlyList<CdContrastBand> bands = CdContrastTestLayout.Bands(parameters);
            Check.Equal(0L, bands[0].FirstByte, "bands start at byte 0");
            Check.Equal(parameters.TotalBytes, bands[^1].EndByte, "bands end at the last byte");
            Check.Equal(CdContrastBandRole.DarkAnchor, bands[0].Role, "innermost band is the dark anchor");
            for (int index = 1; index < bands.Count; index++)
            {
                Check.Equal(bands[index - 1].EndByte, bands[index].FirstByte, "bands are contiguous");
                Check.True((bands[index].FirstByte & 1) == 0, "bands keep whole byte pairs");
            }

            CdContrastBand[] candidates = [.. bands.Where(band => band.Role == CdContrastBandRole.Candidate)];
            Check.Equal(CdContrastTestLayout.LightCandidates.Count, candidates.Length, "one band per candidate");
            for (int index = 0; index < candidates.Length; index++)
            {
                CdContrastBand band = candidates[index];
                Check.Equal(index + 1, band.Number, "candidates are numbered from the centre");
                Check.Equal(CdContrastTestLayout.LightCandidate(index + 1), band.Pattern, "band pattern");
                Check.True(band.OuterRadiusMm - band.InnerRadiusMm >= CdContrastTestLayout.MinimumCandidateWidthMm,
                    "candidate bands are wide enough to see");
                CdContrastBand before = bands[bands.ToList().IndexOf(band) - 1];
                CdContrastBand after = bands[bands.ToList().IndexOf(band) + 1];
                Check.True(before.Role == CdContrastBandRole.Reference && after.Role == CdContrastBandRole.Reference,
                    "every candidate sits between two standard reference bands");
                Check.True(band.InnerRadiusMm >= parameters.InnerRadiusMm
                    && band.OuterRadiusMm <= parameters.RadiusFromByte(parameters.TotalBytes), "bands stay on the disc");
            }
        }

        Check.Equal(3, CdContrastTestLayout.BandOf(CdBytePattern.Parse("F2-44")), "band lookup");
        Check.True(CdContrastTestLayout.BandOf(CdBytePattern.Single(0xAA)) is null, "standard bytes are no band");
        Check.Throws<ArgumentOutOfRangeException>(() => CdContrastTestLayout.LightCandidate(7), "band numbers are 1..6");
    }

    private static void TestSamplerBytes()
    {
        IReadOnlyList<CdContrastBand> bands = CdContrastTestLayout.Bands(Verbatim43438);
        CdContrastTest.Sampler sampler = new(Verbatim43438, bands);
        long labelInk = 0;
        foreach (CdContrastBand band in bands)
        {
            long step = Math.Max(1, (band.EndByte - band.FirstByte) / 200_000) | 1;
            for (long index = band.FirstByte; index < band.EndByte; index += step)
            {
                byte value = sampler.ByteAt(index);
                switch (band.Role)
                {
                    case CdContrastBandRole.DarkAnchor:
                        Check.Equal((byte)0x0D, value, "anchor byte");
                        break;
                    case CdContrastBandRole.Candidate:
                        Check.Equal(band.Pattern.At(index), value, $"band {band.Number} byte");
                        break;
                    default:
                        Check.True(value is 0xAA or 0x0D, "reference bands hold AA and 0D label ink");
                        if (value == 0x0D) labelInk++;
                        break;
                }
            }
        }

        Check.True(labelInk > 0, "band numbers are drawn into the reference bands");
    }

    /// <summary>
    /// Writes a short test track (same layout over fewer sectors), runs it through the interleave
    /// and the CIRC encoder, and checks that frames in the middle of each candidate band are the
    /// candidate's steady-state frame: data slots and inverted parity alike.
    /// </summary>
    private static void TestCandidateBandsAreUniformOnDisc()
    {
        CdDiscParameters shortDisc = new(24.5, 56.8, 4000);
        IReadOnlyList<CdContrastBand> bands = CdContrastTestLayout.Bands(shortDisc);
        using MemoryStream track = new();
        CdContrastTest.GenerateToStream(track, shortDisc, interleave: true, audioByteOrder: CdAudioByteOrder.BigEndian);
        Dictionary<int, byte[]> expected = bands.Where(band => band.Role == CdContrastBandRole.Candidate)
            .ToDictionary(band => band.Number, band => CdChannelModel.SteadyStateFrame(band.Pattern));
        Dictionary<int, int> matched = [];
        CdChannelModel.EncodeF1Stream(track.ToArray(), (frame, f2) =>
        {
            long logicalByte = frame * 24L;
            CdContrastBand? band = bands.FirstOrDefault(candidate => candidate.Role == CdContrastBandRole.Candidate
                && logicalByte >= candidate.FirstByte + (200 * 24) && logicalByte < candidate.EndByte - (200 * 24));
            if (band is null) return;
            Check.True(f2.SequenceEqual(expected[band.Number]), $"band {band.Number} frame {frame} is uniform on disc");
            matched[band.Number] = matched.GetValueOrDefault(band.Number) + 1;
        });
        Check.Equal(CdContrastTestLayout.LightCandidates.Count, matched.Count, "every candidate band was checked");
        Check.True(matched.Values.All(count => count > 500), "each band has many settled frames");
    }

    private static void TestCandidatesAreSafeAndBrighter()
    {
        IReadOnlyList<CdContrastPrediction> predictions = CdContrastTest.Predictions();
        foreach (CdBytePattern candidate in CdContrastTestLayout.LightCandidates)
        {
            CdPairAssessment assessment = CdPatternSafety.AssessPair(CdContrastTestLayout.Dark, candidate);
            Check.True(assessment.Safe, $"0D/{candidate} stays in the envelope ({assessment.FailingScenario})");
            CdContrastPrediction prediction = predictions.Single(item => item.Bytes == candidate.ToString());
            Check.True(prediction.ChangeVersusStandard > 0.015,
                $"{candidate} is predicted to have more transitions than AA ({prediction.ChangeVersusStandard:P1})");
        }
    }

    private static void TestGeneratedFiles()
    {
        string work = Fixtures.CreateWorkDirectory("contrast");
        try
        {
            CdDiscParameters shortDisc = new(24.5, 56.8, 60);
            CdContrastTestResult result = CdContrastTest.Generate(Path.Combine(work, "contrast.wav"), shortDisc);
            Check.Equal(44L + shortDisc.TotalBytes, new FileInfo(result.TrackPath).Length, "WAV length");
            Check.True(result.CueSheetPath is not null && File.Exists(result.CueSheetPath), "CUE written");
            Check.True(new FileInfo(result.LegendPngPath).Length > 1000, "legend image written");
            using JsonDocument legend = JsonDocument.Parse(File.ReadAllText(result.LegendJsonPath));
            Check.Equal(CdContrastTest.LegendFormat, legend.RootElement.GetProperty("format").GetString(), "legend format");
            Check.Equal(CdContrastTestLayout.CandidateSet, legend.RootElement.GetProperty("candidateSet").GetString(), "candidate set");
            Check.Equal(CdContrastTestLayout.LightCandidates.Count,
                legend.RootElement.GetProperty("predictions").GetArrayLength(), "legend predictions");
            Check.True(CdTrackMetadata.TryLoad(result.TrackPath) is not null, "track sidecar for the preview");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
