using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Cd;
using DiscImageStudio.Cd.Music;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// <summary>
/// The design page's music section for picture + music CDs. Songs are prepared in the background
/// (CD-format WAVs as they are, other formats converted by Windows Media Foundation), one at a
/// time, as soon as they are added or a project is opened. Export and burn wait for them with
/// <see cref="EnsurePreparedAsync"/>.
/// </summary>
public sealed partial class MusicViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private readonly Dictionary<CdMusicTrackItem, Task> _preparations = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MusicViewModel(ShellViewModel shell, DiscParametersState state)
    {
        _shell = shell;
        _state = state;
        AddCommand = new RelayCommand(Add, CanAdd);
        RemoveCommand = new RelayCommand<CdMusicTrackItem>(Remove, item => item is not null && !_shell.IsBusy);
        MoveUpCommand = new RelayCommand<CdMusicTrackItem>(item => Move(item, -1),
            item => item is not null && !_shell.IsBusy && Tracks.IndexOf(item) > 0);
        MoveDownCommand = new RelayCommand<CdMusicTrackItem>(item => Move(item, 1),
            item => item is not null && !_shell.IsBusy && Tracks.IndexOf(item) is int index && index >= 0 && index < Tracks.Count - 1);
        Tracks.CollectionChanged += OnTracksChanged;
        _shell.BusyChanged += NotifyCommands;
        _state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DiscParametersState.CdDataDirectory))
            {
                AddCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanAddMusic));
            }

            if (args.PropertyName is nameof(DiscParametersState.HasCdMusic))
            {
                OnPropertyChanged(nameof(CanChooseFolder));
            }

            if (args.PropertyName is nameof(DiscParametersState.CdMusicAtOuterRing))
            {
                OnPropertyChanged(nameof(PlacementHint));
            }

            if (args.PropertyName is nameof(DiscParametersState.CdMusicPreparedRevision)
                or nameof(DiscParametersState.CdMusicAtOuterRing) or nameof(DiscParametersState.CdMusicGap)
                or nameof(DiscParametersState.CdDataDirectory)
                or nameof(DiscParametersState.CdInnerRadius) or nameof(DiscParametersState.CdOuterRadius)
                or nameof(DiscParametersState.CdSectors) or nameof(DiscParametersState.CdUsableSectors)
                or nameof(DiscParametersState.CdPitchLinear) or nameof(DiscParametersState.CdPitchQuadratic)
                or nameof(DiscParametersState.CdPitchCubic))
            {
                RefreshText();
            }
        };
        RefreshText();
    }

    public ObservableCollection<CdMusicTrackItem> Tracks => _state.CdMusicTracks;

    public DiscParametersState State => _state;

    public RelayCommand AddCommand { get; }

    public RelayCommand<CdMusicTrackItem> RemoveCommand { get; }

    public RelayCommand<CdMusicTrackItem> MoveUpCommand { get; }

    public RelayCommand<CdMusicTrackItem> MoveDownCommand { get; }

    /// <summary>A CD holds either songs or on-disc files.</summary>
    public bool CanAddMusic => !_state.HasCdFiles;

    /// <summary>The on-disc folder is disabled while the disc has songs.</summary>
    public bool CanChooseFolder => !_state.HasCdMusic;

    public string PlacementHint => _state.CdMusicAtOuterRing ? L.T("Music.OuterHint") : L.T("Music.InnerHint");

    /// <summary>"3 songs · 12:41 · picture 24.8–53.9 mm, 84 % of the disc"; empty without songs.</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Capacity, unreadable songs, a small picture area, or the noise track of the
    /// inner-ring layout; empty when there is nothing to warn about.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string _warning = string.Empty;

    public bool HasWarning => Warning.Length > 0;

    /// <summary>The music layout of the current songs and disc, or null while songs are being
    /// prepared, the geometry is incomplete, or the songs do not fit.</summary>
    public CdMusicLayout? Layout { get; private set; }

    public void AddFiles(IEnumerable<string> paths)
    {
        if (!CanAdd())
        {
            return;
        }

        foreach (string path in paths)
        {
            if (Tracks.Count >= CdMusicLayout.MaximumSongs)
            {
                _shell.ShowToast(L.T("歌曲太多"), L.F($"一张 CD 在图案之外最多放 {CdMusicLayout.MaximumSongs} 首歌。"), ToastKind.Info);
                break;
            }

            Tracks.Add(new CdMusicTrackItem(path));
        }
    }

    /// <summary>Waits for every song to be prepared; throws with the song's message when one
    /// cannot be read.</summary>
    public async Task EnsurePreparedAsync()
    {
        while (_preparations.Values.Where(task => !task.IsCompleted).ToArray() is { Length: > 0 } running)
        {
            await Task.WhenAll(running);
        }

        if (Tracks.FirstOrDefault(track => track.HasError) is { } failed)
        {
            throw new InvalidOperationException(L.F($"无法使用歌曲「{failed.Title}」：{failed.Error}"));
        }
    }

    internal void Relocalize()
    {
        foreach (CdMusicTrackItem track in Tracks) track.Relocalize();
        OnPropertyChanged(nameof(PlacementHint));
        RefreshText();
    }

    private bool CanAdd() => !_shell.IsBusy && !_state.HasCdFiles && Tracks.Count < CdMusicLayout.MaximumSongs;

    private void Add()
    {
        string[]? picked = _shell.Dialogs?.PickMusic();
        if (picked is { Length: > 0 })
        {
            AddFiles(picked);
        }
    }

    private void Remove(CdMusicTrackItem? item)
    {
        if (item is not null)
        {
            Tracks.Remove(item);
        }
    }

    private void Move(CdMusicTrackItem? item, int step)
    {
        if (item is null) return;
        int index = Tracks.IndexOf(item);
        int target = index + step;
        if (index < 0 || target < 0 || target >= Tracks.Count) return;
        Tracks.Move(index, target);
    }

    private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (CdMusicTrackItem item in e.NewItems)
            {
                if (!_preparations.ContainsKey(item))
                {
                    _preparations[item] = PrepareAsync(item);
                }
            }
        }

        foreach (CdMusicTrackItem stale in _preparations.Keys.Where(item => !Tracks.Contains(item)).ToArray())
        {
            if (_preparations[stale].IsCompleted) _preparations.Remove(stale);
        }

        NotifyCommands();
        RefreshText();
    }

    private async Task PrepareAsync(CdMusicTrackItem item)
    {
        await _gate.WaitAsync();
        try
        {
            if (!Tracks.Contains(item))
            {
                return;
            }

            string path = item.SourcePath;
            string title = item.Title;
            item.Prepared = await Task.Run(() => MusicConversion.Prepare(path, title, CancellationToken.None));
        }
        catch (Exception exception) when (exception is MusicConversionException or IOException
                                              or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            item.Error = exception.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void NotifyCommands()
    {
        AddCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddMusic));
    }

    private void RefreshText()
    {
        Layout = null;
        if (!_state.HasCdMusic)
        {
            Summary = string.Empty;
            Warning = string.Empty;
            OnPropertyChanged(nameof(Layout));
            return;
        }

        if (Tracks.FirstOrDefault(track => track.HasError) is { } failed)
        {
            Summary = SongCount();
            Warning = L.F($"无法使用歌曲「{failed.Title}」：{failed.Error}");
        }
        else if (_state.PreparedCdMusic is not { } songs)
        {
            Summary = SongCount() + L.T(" · 正在准备歌曲…");
            Warning = string.Empty;
        }
        else
        {
            try
            {
                CdMusicLayout layout = CdMusicLayout.Plan(songs, ReadParameters(), _state.CdMusicPlacement,
                    _state.CdMusicGapSeconds);
                Layout = layout;
                Summary = SongCount() + " · " + CdMusicLayout.Clock(layout.MusicSectors)
                    + L.F($" · 图案 {layout.PictureInnerRadiusMm:0.0}–{layout.PictureOuterRadiusMm:0.0} mm，占盘面 {layout.PictureShare:P0}");
                Warning = layout.Placement == CdMusicPlacement.InnerRing
                    ? L.T("图案是最后一条音轨，播放时是很响的噪声：听完歌曲后请停止播放。")
                    : layout.PictureShare < 0.25
                        ? L.T("歌曲占去了大部分盘面，图案只剩很小一块。")
                        : string.Empty;
            }
            catch (CdMusicCapacityException exception)
            {
                Summary = SongCount();
                Warning = L.T("歌曲太长，图案没有空间了：请移除一首歌。") + " " + exception.Message;
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                // Disc fields are incomplete while a project or preset is applied.
                Summary = SongCount();
                Warning = string.Empty;
            }
        }

        OnPropertyChanged(nameof(Layout));
    }

    private string SongCount() => Tracks.Count == 1 ? L.T("1 首歌") : L.F($"{Tracks.Count} 首歌");

    private CdDiscParameters ReadParameters() => new(
        ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, L.T("内半径")),
        ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, L.T("外半径")),
        ParameterParser.ParsePositiveLong(_state.CdSectors, L.T("总扇区数")),
        PitchLinear: ParameterParser.ParseDouble(_state.CdPitchLinear, L.T("相对一次项")),
        PitchQuadratic: ParameterParser.ParseDouble(_state.CdPitchQuadratic, L.T("相对二次项")),
        PitchCubic: ParameterParser.ParseDouble(_state.CdPitchCubic, L.T("相对三次项")),
        UsableSectors: _state.GetUsableSectors(Core.OpticalDiscFamily.CompactDisc));
}
