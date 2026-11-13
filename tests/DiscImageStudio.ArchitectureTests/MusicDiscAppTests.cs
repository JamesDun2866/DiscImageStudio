using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Cd.Music;
using DiscImageStudio.Core;
using DiscImageStudio.Localization;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

/// <summary>
/// App wiring of picture + music CDs: the song list in the shared state and the project file, the
/// design page's music card and guide ring, and the burn request, whose bytes equal the exported
/// WAV and whose track layout native IMAPI accepts. The engines are covered by CdTests and
/// CdMusicImapiLayoutTests.
/// </summary>
internal static class MusicDiscAppTests
{
    private static readonly CdDiscParameters SmallDisc = new(24.5, 30.0, 4000);

    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dis-music-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string songA = WriteCdWave(Path.Combine(directory, "Song A.wav"), 617_400, 1);
            string songB = WriteCdWave(Path.Combine(directory, "Song B.wav"), 176_400, 2);
            string image = Path.Combine(directory, "gradient.png");
            WriteGradient(image);
            TestStateAndProject(directory, songA, songB);
            TestBurnRequests(directory, image, songA, songB);
            WpfTestHost.Run(() => CheckPages(songA, songB));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("music-disc-app: state, project, burn requests (bytes = export, IMAPI layout), "
            + "design card, guide and burn summary passed (no hardware)");
    }

    private static void TestStateAndProject(string directory, string songA, string songB)
    {
        DiscParametersState state = new();
        string empty = state.CdMusicSignature;
        Require(!state.HasCdMusic && state.CreateCdMusicRequest() is null, "a new state has no songs");
        state.CdMusicTracks.Add(new CdMusicTrackItem(songA));
        state.CdMusicTracks.Add(new CdMusicTrackItem(songB, "Second"));
        Require(state.HasCdMusic && state.CdMusicTracks[0].Number == 1 && state.CdMusicTracks[1].Number == 2,
            "songs are numbered in play order");
        Require(state.CdMusicSignature != empty, "the signature follows the song list");
        RequireThrows<InvalidOperationException>(() => state.CreateCdMusicRequest(), "unprepared songs cannot be written");
        foreach (CdMusicTrackItem item in state.CdMusicTracks)
        {
            item.Prepared = CdMusicSource.FromWave(item.SourcePath, item.Title);
        }

        CdMusicRequest? request = state.CreateCdMusicRequest();
        Require(request is { Placement: CdMusicPlacement.OuterRing, GapSeconds: 2 } && request.Songs.Count == 2
            && request.Songs[1].Title == "Second", "prepared songs form the request (outer ring, 2 s gap by default)");
        state.CdOutputPath = Path.Combine(directory, "disc.raw");
        state.NormalizeCdOutputFormat();
        Require(state.CdOutputPath.EndsWith(".wav", StringComparison.Ordinal), "a disc with songs is written as WAV");

        ProjectDocument document = Project(null);
        state.CdMusicAtOuterRing = false;
        state.CdMusicGap = false;
        ProjectCdSettings? captured = ProjectSession.CaptureCd(document, state);
        Require(captured is { DarkBytes: null, LightBytes: null, Music: { Placement: "inner", GapSeconds: 0 } music }
            && music.Tracks.Count == 2 && music.Tracks[0].Title is null && music.Tracks[1].Title == "Second",
            "the project keeps the songs, placement and gap; default titles are omitted");
        string path = Path.Combine(directory, "music.discproj");
        ProjectJsonStore.Save(path, document with { Cd = captured });
        ProjectDocument loaded = ProjectJsonStore.Load(path);
        DiscParametersState reopened = new();
        ProjectSession.ApplyToState(loaded, reopened);
        Require(reopened.CdMusicTracks.Select(track => (track.SourcePath, track.Title))
                .SequenceEqual([(Path.GetFullPath(songA), "Song A"), (Path.GetFullPath(songB), "Second")])
            && !reopened.CdMusicAtOuterRing && !reopened.CdMusicGap, "opening the project restores the songs");
        ProjectSession.ApplyToState(Project(null), reopened);
        Require(!reopened.HasCdMusic && reopened.CdMusicAtOuterRing && reopened.CdMusicGap,
            "a project without songs clears them and restores the defaults");
        RequireThrows<Exception>(() => ProjectJsonStore.Validate(document with
        {
            Cd = captured! with { Music = captured.Music! with { Placement = "middle" } },
        }), "an unknown placement is rejected");
        RequireThrows<Exception>(() => ProjectJsonStore.Validate(document with { DiscFamily = ProjectDocument.DvdFamily, Cd = captured }),
            "DVD projects cannot carry songs");
    }

    private static void TestBurnRequests(string directory, string image, string songA, string songB)
    {
        CdMusicSource[] songs = [CdMusicSource.FromWave(songA), CdMusicSource.FromWave(songB)];
        foreach (CdMusicPlacement placement in new[] { CdMusicPlacement.OuterRing, CdMusicPlacement.InnerRing })
        {
            CdMusicRequest music = new(songs, placement);
            OpticalBurnRequest request = BurnViewModel.CreateCdBurnRequest(image, SmallDisc, null, "CD_IMAGE", false, true,
                ToneProfile.Photo, "device", null, CdPalette.FromText(null, "F2-A8"), music);
            request.Validate();
            CdMusicLayout layout = CdMusicLayout.Plan(songs, SmallDisc, placement);
            Require(request.CdTrackLayout is { } tracks && tracks.SpecialPregapSectors == layout.SpecialPregapSectors
                && tracks.TrackSectors.SequenceEqual(layout.TrackSectors) && request.ContentLength == SmallDisc.TotalBytes,
                $"{placement}: the burn request carries the planned tracks");

            using MemoryStream burned = new();
            request.ProduceContent(burned, CancellationToken.None);
            string export = Path.Combine(directory, $"export-{placement}.wav");
            CdImageGenerator.Generate(new CdImageRequest(image, export, SmallDisc, ToneProfile.Photo,
                Palette: CdPalette.FromText(null, "F2-A8"), Music: music));
            byte[] wave = File.ReadAllBytes(export);
            Require(burned.ToArray().AsSpan().SequenceEqual(wave.AsSpan(44)), $"{placement}: the burned bytes equal the exported WAV");

            using GeneratedContentComStream content = new(request.ContentLength, request.ProduceContent, null, CancellationToken.None);
            content.WaitUntilPrebuffered(CancellationToken.None);
            using DynamicCdRawImageSession session = new();
            IStream result = ((ICdRawImageSession)session).CreateImage(content, CdRawSectorType.SubcodeIsCooked, request.CdTrackLayout);
            Require(session.StartOfLeadout == SmallDisc.OutputSectors,
                $"{placement}: native IMAPI accepts the layout and ends at the picture-only disc's lead-out");
            if (Marshal.IsComObject(result)) Marshal.FinalReleaseComObject(result);
        }
    }

    private static void CheckPages(string songA, string songB)
    {
        string originalLanguage = L.CurrentLanguage;
        try
        {
            L.SetLanguage(L.English);
            ShellViewModel shell = new(Dispatcher.CurrentDispatcher);
            ProjectDocument document = Project(null);
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.CurrentProject))!.SetValue(shell, document);
            shell.IsProjectOpen = true;
            shell.Design.SetDiscFamily(OpticalDiscFamily.CompactDisc);
            ProjectSession.ApplyToState(document, shell.State);
            shell.IsDirty = false;

            shell.Design.Music.AddFiles([songA, songB]);
            PumpUntil(() => shell.State.PreparedCdMusic is { Count: 2 }, "both songs are prepared");
            Require(shell.IsDirty, "adding songs marks the project dirty");
            Require(!shell.Design.Music.CanChooseFolder, "the on-disc folder is disabled while the disc has songs");
            PumpUntil(() => shell.Design.DataRegions is { Count: 1 } && shell.Design.Music.Layout is not null,
                "the guide ring is planned");
            CdMusicLayout layout = shell.Design.Music.Layout!;
            DesignViewModel.DataRegionMm ring = shell.Design.DataRegions![0];
            Require(Math.Abs(ring.InnerRadiusMm - layout.MusicInnerRadiusMm) < 1e-9
                && Math.Abs(ring.OuterRadiusMm - layout.MusicOuterRadiusMm) < 1e-9, "the guide marks the music ring");
            Require(shell.Design.Music.Summary.StartsWith("2 songs", StringComparison.Ordinal)
                && shell.Design.Music.Summary.Contains("picture", StringComparison.Ordinal), "the summary names songs and picture area");

            DesignView view = new(shell.Design);
            Size size = new(1440, 1400);
            view.Measure(size);
            view.Arrange(new Rect(size));
            view.UpdateLayout();
            ItemsControl list = Descendants<ItemsControl>(view)
                .Single(items => ReferenceEquals(items.ItemsSource, shell.Design.Music.Tracks));
            Require(list.Items.Count == 2, "the music card lists both songs");
            UiLayoutSnapshot.Save(view, "music-disc-design-page-en");

            shell.Design.Music.MoveDownCommand.Execute(shell.State.CdMusicTracks[0]);
            Require(shell.State.CdMusicTracks[0].Title == "Song B" && shell.State.CdMusicTracks[0].Number == 1,
                "moving a song renumbers the list");
            shell.State.CdMusicAtOuterRing = false;
            Pump();
            Require(shell.Design.Music.HasWarning, "the inner-ring layout warns about the noise track");

            shell.Burn.UpdateSourceSummary();
            Require(shell.Burn.SourceDetails.Contains("2 songs", StringComparison.Ordinal), "the burn source lists the songs");
            shell.Burn.ConfirmChecked = true;
            shell.State.CdMusicGap = false;
            Require(!shell.Burn.ConfirmChecked, "changing the songs' gap invalidates the burn confirmation");

            shell.Design.Music.RemoveCommand.Execute(shell.State.CdMusicTracks[0]);
            shell.Design.Music.RemoveCommand.Execute(shell.State.CdMusicTracks[0]);
            Require(!shell.State.HasCdMusic && shell.Design.Music.CanChooseFolder, "removing every song frees the folder again");
        }
        finally
        {
            L.SetLanguage(originalLanguage);
        }
    }

    private static ProjectDocument Project(ProjectCdSettings? cd)
        => new(ProjectJsonStore.CurrentSchemaVersion, "Music", ProjectDocument.CdFamily,
            new ProjectGeometry(24.837775, 58.020875, 359_848, UsableSectors: 359_846), [],
            new ProjectOutputs(Path.Combine(Path.GetTempPath(), "music.wav"), true, string.Empty, "CD_IMAGE", true),
            null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, ToneProfile.Photo, null, cd);

    private static string WriteCdWave(string path, int audioBytes, int seed)
    {
        byte[] file = new byte[44 + audioBytes];
        new Random(seed).NextBytes(file.AsSpan(44));
        "RIFF"u8.CopyTo(file);
        BitConverter.TryWriteBytes(file.AsSpan(4), 36 + audioBytes);
        "WAVEfmt "u8.CopyTo(file.AsSpan(8));
        BitConverter.TryWriteBytes(file.AsSpan(16), 16);
        BitConverter.TryWriteBytes(file.AsSpan(20), (short)1);
        BitConverter.TryWriteBytes(file.AsSpan(22), (short)2);
        BitConverter.TryWriteBytes(file.AsSpan(24), 44100);
        BitConverter.TryWriteBytes(file.AsSpan(28), 176400);
        BitConverter.TryWriteBytes(file.AsSpan(32), (short)4);
        BitConverter.TryWriteBytes(file.AsSpan(34), (short)16);
        "data"u8.CopyTo(file.AsSpan(36));
        BitConverter.TryWriteBytes(file.AsSpan(40), audioBytes);
        File.WriteAllBytes(path, file);
        return path;
    }

    private static void WriteGradient(string path)
    {
        const int size = 128;
        byte[] pixels = new byte[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                pixels[(y * size) + x] = (byte)(x * 2);
        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static void Pump()
        => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void PumpUntil(Func<bool> condition, string message)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(60)) throw new InvalidOperationException("Music disc app: timed out: " + message);
            Pump();
            Thread.Sleep(20);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Music disc app: " + message);
    }

    private static void RequireThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Music disc app: " + message);
    }
}
