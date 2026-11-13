using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Cd.Contrast;
using DiscImageStudio.Controls;
using DiscImageStudio.Core;
using DiscImageStudio.Localization;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

/// <summary>
/// App wiring of the CD contrast tools: custom light disc bytes in the tone state, project and
/// burn requests, and the burn page's contrast-test switch. The engines themselves are covered
/// by tests/DiscImageStudio.CdTests on every platform.
/// </summary>
internal static class ContrastToolsTests
{
    internal static void Run()
    {
        TestStateCoercion();
        TestProjectSession();
        TestBurnRequests();
        WpfTestHost.Run(() => CheckPagesAsync().GetAwaiter().GetResult());
        Console.WriteLine("contrast-tools: state, project, burn requests and pages passed (no hardware)");
    }

    private static void TestStateCoercion()
    {
        DiscParametersState state = new() { ToneLevels = ToneProfile.FourLevels };
        Require(!state.IsCdCustomPalette && state.CdPalette.IsStandard, "new state uses the standard bytes");
        state.CdLightBytes = "F2-A8";
        Require(state.IsCdCustomPalette && state.ToneLevels == ToneProfile.TwoLevels,
            "choosing a test band switches 4-level tone off");
        Require(state.CdPalette == CdPalette.FromText(null, "F2-A8"), "palette follows the light bytes");
        state.ToneLevels = ToneProfile.FourLevels;
        Require(!state.IsCdCustomPalette && state.CdLightBytes == "AA",
            "switching 4 levels on returns to the standard bytes");
        state.CdLightBytes = "not hex";
        Require(state.CdPalette.IsStandard, "unreadable text falls back to the standard palette");
    }

    private static void TestProjectSession()
    {
        DiscParametersState state = new();
        ProjectDocument custom = Project(new ProjectCdSettings(LightBytes: "A8-F6"));
        ProjectSession.ApplyToState(custom, state);
        Require(state.CdLightBytes == "A8-F6" && state.CdDarkBytes == "0D", "opening a project applies its disc bytes");
        Require(ProjectSession.CaptureCd(custom, state) == new ProjectCdSettings(null, "A8-F6"),
            "saving keeps only the custom light bytes");
        ProjectSession.ApplyToState(Project(null), state);
        Require(state.CdLightBytes == "AA" && ProjectSession.CaptureCd(custom, state) is null,
            "standard projects reset the bytes and save no cd section");
        ProjectDocument dvd = Project(null) with { DiscFamily = ProjectDocument.DvdFamily };
        state.CdLightBytes = "B6";
        Require(ProjectSession.CaptureCd(dvd, state) is null, "DVD projects never carry a cd section");
    }

    private static void TestBurnRequests()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dis-contrast-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CdDiscParameters parameters = new(24.5, 56.8, 60);
            OpticalBurnRequest contrast = BurnViewModel.CreateContrastTestBurnRequest(parameters, "device", null);
            Require(contrast.MediaKind == OpticalBurnMediaKind.CdAudio && contrast.ContentLength == parameters.TotalBytes,
                "the contrast test burns as one audio track");
            using MemoryStream burned = new();
            contrast.ProduceContent(burned, CancellationToken.None);
            using MemoryStream expected = new();
            CdContrastTest.GenerateToStream(expected, parameters, interleave: true, CdAudioByteOrder.LittleEndian);
            Require(burned.ToArray().AsSpan().SequenceEqual(expected.ToArray()),
                "the burned contrast test equals the exported track");

            string image = Path.Combine(directory, "gradient.png");
            WriteGradient(image);
            CdPalette palette = CdPalette.FromText(null, "F2-40");
            OpticalBurnRequest picture = BurnViewModel.CreateCdBurnRequest(image, parameters, null, "CD_IMAGE",
                false, true, ToneProfile.Photo, "device", null, palette);
            using MemoryStream pictureBytes = new();
            picture.ProduceContent(pictureBytes, CancellationToken.None);
            using MemoryStream pictureExpected = new();
            CdTrackGenerator.GenerateToStream(image, pictureExpected, parameters, true,
                audioByteOrder: CdAudioByteOrder.LittleEndian, tone: ToneProfile.Photo, palette: palette);
            Require(pictureBytes.ToArray().AsSpan().SequenceEqual(pictureExpected.ToArray()),
                "picture burns use the custom disc bytes");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CheckPagesAsync()
    {
        string originalLanguage = L.CurrentLanguage;
        Window? host = null;
        try
        {
            L.SetLanguage(L.English);
            ShellViewModel shell = new(Dispatcher.CurrentDispatcher, new NoDeviceBurner());
            ProjectDocument document = Project(null);
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.CurrentProject))!.SetValue(shell, document);
            shell.IsProjectOpen = true;
            shell.Design.SetDiscFamily(OpticalDiscFamily.CompactDisc);
            ProjectSession.ApplyToState(document, shell.State);
            shell.IsDirty = false;

            TonePanel tone = new() { DataContext = shell.Design.Tone, Width = 360 };
            host = new Window
            {
                DataContext = shell, Content = tone, Width = 420, Height = 900, Left = -10000, Top = -10000,
                ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            };
            host.Show();
            Pump();
            ComboBox picker = Descendants<ComboBox>(tone)
                .Single(combo => ReferenceEquals(combo.ItemsSource, shell.Design.Tone.CdLightBytesOptions));
            Require(picker.Items.Count == CdContrastTestLayout.LightCandidates.Count + 1,
                "the picker lists the standard bytes and every test band");
            picker.SetCurrentValue(Selector.SelectedIndexProperty, 1);
            Pump();
            Require(shell.State.CdLightBytes == CdContrastTestLayout.LightCandidate(1).ToString() && shell.IsDirty,
                "picking band 1 sets the light bytes and marks the project dirty");
            UiLayoutSnapshot.Save(tone, "contrast-tools-tone-panel-en");

            BurnView burn = new(shell.Burn);
            host.Content = burn;
            host.Width = 1100;
            Pump();
            Require(shell.Burn.CanBurnContrastTest, "CD projects offer the contrast test");
            shell.Burn.UpdateSourceSummary();
            shell.Burn.ConfirmChecked = true;
            shell.State.CdLightBytes = "B6";
            Require(!shell.Burn.ConfirmChecked, "changing the disc bytes invalidates the burn confirmation");
            shell.Burn.ConfirmChecked = true;
            CheckBox toggle = Descendants<CheckBox>(burn)
                .Single(box => box.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path
                    == nameof(BurnViewModel.BurnContrastTest));
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Pump();
            Require(shell.Burn.BurnContrastTest && !shell.Burn.ConfirmChecked,
                "switching to the contrast test invalidates the confirmation");
            Require(shell.Burn.SourceDetails.Contains("1 F2-A8", StringComparison.Ordinal),
                "the burn source lists the test bands");
            UiLayoutSnapshot.Save(burn, "contrast-tools-burn-page-en");

            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.CurrentProject))!
                .SetValue(shell, document with { DiscFamily = ProjectDocument.DvdFamily });
            Require(!shell.Burn.CanBurnContrastTest, "DVD projects do not offer the CD contrast test");
            await Task.CompletedTask;
        }
        finally
        {
            host?.Close();
            L.SetLanguage(originalLanguage);
        }
    }

    private static ProjectDocument Project(ProjectCdSettings? cd)
        => new(ProjectJsonStore.CurrentSchemaVersion, "Contrast", ProjectDocument.CdFamily,
            new ProjectGeometry(24.837775, 58.020875, 359_848, UsableSectors: 359_846), [],
            new ProjectOutputs(Path.Combine(Path.GetTempPath(), "contrast.wav"), true, string.Empty, "CD_IMAGE", true),
            null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, ToneProfile.Photo, null, cd);

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
        if (!condition) throw new InvalidOperationException("Contrast tools: " + message);
    }

    private sealed class NoDeviceBurner : IOpticalDiscBurner
    {
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalBurnDevice>>([]);
        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind,
            CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId,
            OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpticalWriteSpeed>>([]);
        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request,
            IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The contrast tools test must not start a burn.");
    }
}
