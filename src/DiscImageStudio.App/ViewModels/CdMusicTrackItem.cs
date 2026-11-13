using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.Cd.Music;
using DiscImageStudio.Localization;

namespace DiscImageStudio.ViewModels;

/// <summary>
/// One song of a picture + music CD: the file the user chose and, once prepared, the CD-format
/// WAV the generators read (the file itself, or a converted copy in the app cache). The project
/// stores the chosen file; preparation runs again when a project is opened.
/// </summary>
public sealed partial class CdMusicTrackItem : ObservableObject
{
    public CdMusicTrackItem(string sourcePath, string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        SourcePath = Path.GetFullPath(sourcePath);
        Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(SourcePath) : title.Trim();
    }

    public string SourcePath { get; }

    public string Title { get; }

    /// <summary>The CD-format WAV and its PCM range; null while converting or after a failure.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreparing), nameof(HasError), nameof(StatusText))]
    private CdMusicSource? _prepared;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreparing), nameof(HasError), nameof(StatusText))]
    private string? _error;

    /// <summary>1-based play position, shown in the list.</summary>
    [ObservableProperty]
    private int _number;

    public bool IsPreparing => Prepared is null && Error is null;

    public bool HasError => Error is not null;

    public string StatusText => Prepared is { } song
        ? $"{(int)song.Duration.TotalMinutes}:{song.Duration.Seconds:00}"
        : Error is not null ? L.T("无法读取") : L.T("正在转换…");

    internal void Relocalize() => OnPropertyChanged(nameof(StatusText));
}
