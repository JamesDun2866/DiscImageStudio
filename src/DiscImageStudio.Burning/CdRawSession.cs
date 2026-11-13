using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

internal interface ICdRawSession
{
    bool MediaPhysicallyBlank { get; }

    long LastPossibleStartOfLeadout { get; }

    IReadOnlyList<int> SupportedSectorTypes { get; }

    int CurrentWriteSpeed { get; }

    bool CurrentRotationTypeIsPureCav { get; }

    void PrepareMedia();

    void SetRequestedSectorType(int sectorType);

    void SetWriteSpeed(int sectorsPerSecond, bool rotationTypeIsPureCav);

    void WriteMedia(IStream content);

    void CancelWrite();

    void ReleaseMedia();

    // Read only while prepared and before writing. Fakes need not provide telemetry.
    string? GetDiagnosticSummary() => null;
}

// IMAPI_FORMAT2_RAW_CD_DATA_SECTOR_TYPE (imapi2.h). Both values use 2448-byte sectors:
// 2352 main-channel bytes followed by 96 P-W subchannel bytes. PQ_ONLY (1) uses 2368-byte
// sectors, which the DAO image streams do not support.
// https://learn.microsoft.com/windows/win32/api/imapi2/ne-imapi2-imapi_format2_raw_cd_data_sector_type
internal static class CdRawSectorType
{
    internal const int SubcodeIsCooked = 2;
    internal const int SubcodeIsRaw = 3;

    internal static bool IsSupported(int sectorType)
        => sectorType is SubcodeIsCooked or SubcodeIsRaw;
}

internal interface ICdRawImageSession : IDisposable
{
    long StartOfLeadout { get; }

    // sectorType must equal the type requested from the RawCD format. IMAPI cannot
    // detect a mismatch because both supported layouts use 2448-byte sectors.
    IStream CreateImage(IStream content, int sectorType);

    // A multi-track layout splits the content into tracks (and an optional hidden first-track
    // pregap). Only the IMAPI audio image creator supports it; null keeps the single track.
    IStream CreateImage(IStream content, int sectorType, CdDaoTrackLayout? layout)
        => layout is null
            ? CreateImage(content, sectorType)
            : throw new NotSupportedException("This CD image session writes a single track only.");
}

internal sealed class DynamicCdRawSession(object format, Action<object>? reportWriteProgress = null) : ICdRawSession
{
    private readonly dynamic _format = format;

    public bool MediaPhysicallyBlank => Convert.ToBoolean(_format.MediaPhysicallyBlank);

    public long LastPossibleStartOfLeadout =>
        Convert.ToInt64(_format.LastPossibleStartOfLeadout);

    public IReadOnlyList<int> SupportedSectorTypes
    {
        get
        {
            if (_format.SupportedSectorTypes is not Array values)
            {
                return [];
            }

            return values.Cast<object>().Select(Convert.ToInt32).ToArray();
        }
    }

    public int CurrentWriteSpeed => Convert.ToInt32(_format.CurrentWriteSpeed);

    public bool CurrentRotationTypeIsPureCav =>
        Convert.ToBoolean(_format.CurrentRotationTypeIsPureCAV);

    public void PrepareMedia() => _format.PrepareMedia();

    public void SetRequestedSectorType(int sectorType) =>
        _format.RequestedSectorType = sectorType;

    public void SetWriteSpeed(int sectorsPerSecond, bool rotationTypeIsPureCav) =>
        _format.SetWriteSpeed(sectorsPerSecond, rotationTypeIsPureCav);

    public void WriteMedia(IStream content)
    {
        if (reportWriteProgress is null)
        {
            _format.WriteMedia(content);
            return;
        }
        // DDiscFormat2RawCDEvents.Update (Windows SDK imapi2.idl).
        Guid events = new("27354142-7F64-5B0F-8F00-5D77AFBE261E");
        Action<object, object> update = (_, args) => reportWriteProgress(args);
        bool subscribed = false;
        try
        {
            ComEventsHelper.Combine((object)_format, events, 0x200, update);
            subscribed = true;
        }
        catch (COMException) { /* Optional telemetry must not prevent writing. */ }
        try { _format.WriteMedia(content); }
        finally
        {
            if (subscribed)
            {
                try { ComEventsHelper.Remove((object)_format, events, 0x200, update); }
                catch (COMException) { /* Preserve any write error. */ }
            }
        }
    }

    public void CancelWrite() => _format.CancelWrite();

    public void ReleaseMedia() => _format.ReleaseMedia();

    public string GetDiagnosticSummary() => BurnDeviceDiagnostics.DescribePreparedMedia((object)_format);
}

internal sealed class DynamicCdRawImageSession : ICdRawImageSession
{
    private const int AudioSectorType = 0;

    private readonly dynamic _creator;
    private object? _resultImage;
    private bool _disposed;

    internal DynamicCdRawImageSession()
    {
        Type type = Type.GetTypeFromProgID(
            "IMAPI2.MsftRawCDImageCreator",
            throwOnError: true)!;
        _creator = Activator.CreateInstance(type)!;
    }

    public long StartOfLeadout => Convert.ToInt64(_creator.StartOfLeadout);

    internal int ResultingImageType => Convert.ToInt32(_creator.ResultingImageType);

    public IStream CreateImage(IStream audioContent, int sectorType)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(audioContent);
        if (!CdRawSectorType.IsSupported(sectorType))
        {
            throw new ArgumentOutOfRangeException(nameof(sectorType), sectorType,
                "CD DAO images require cooked R-W or raw P-W 2448-byte sectors.");
        }

        // The creator becomes read-only after CreateResultImage. It writes no R-W data, so
        // cooked and raw images carry the same bytes. This value never reaches the recorder:
        // the caller must request the same type via IDiscFormat2RawCD.RequestedSectorType
        // (default: cooked R-W), which is what announces the subchannel layout.
        _creator.ResultingImageType = sectorType;
        // AddTrack expects WAV-order PCM without a header. The RAW image creator
        // preserves the supplied sample bytes; it does not correct big-endian input.
        _creator.AddTrack(AudioSectorType, audioContent);
        _resultImage = _creator.CreateResultImage();
        return (IStream)_resultImage;
    }

    /// <summary>
    /// Builds a multi-track audio image from one sequential content stream: an optional special
    /// pregap (track 1's index 00, the hidden area players skip) and then the tracks, gapless.
    /// Every track's position is compared with <paramref name="layout"/> before the image is
    /// returned, because a picture drawn into the tracks only lines up at its planned LBAs.
    /// </summary>
    public IStream CreateImage(IStream content, int sectorType, CdDaoTrackLayout? layout)
    {
        if (layout is null)
        {
            return CreateImage(content, sectorType);
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(content);
        if (!CdRawSectorType.IsSupported(sectorType))
        {
            throw new ArgumentOutOfRangeException(nameof(sectorType), sectorType,
                "CD DAO images require cooked R-W or raw P-W 2448-byte sectors.");
        }

        if (_resultImage is not null)
        {
            throw new InvalidOperationException("The CD DAO image has already been created.");
        }

        layout.Validate();
        content.Stat(out STATSTG stat, 1);
        if (stat.cbSize != layout.ContentBytes)
        {
            throw new ArgumentException(
                $"The CD content has {stat.cbSize} bytes; the track layout needs {layout.ContentBytes}.",
                nameof(content));
        }

        _creator.ResultingImageType = sectorType;
        // Gapless is IMAPI's default; set it explicitly because a 2-second gap per track would
        // move every later track, and the picture with it, by 150 sectors.
        _creator.DisableGaplessAudio = false;
        CdTrackSliceStream.SharedSource source = new(content);
        long offset = 0;
        if (layout.SpecialPregapSectors > 0)
        {
            long pregapBytes = layout.SpecialPregapSectors * SectorBytes;
            _creator.AddSpecialPregap(new CdTrackSliceStream(source, offset, pregapBytes));
            offset += pregapBytes;
        }

        List<object> trackIndexes = new(layout.TrackSectors.Count);
        foreach (long sectors in layout.TrackSectors)
        {
            long bytes = sectors * SectorBytes;
            trackIndexes.Add((object)_creator.AddTrack(AudioSectorType, new CdTrackSliceStream(source, offset, bytes)));
            offset += bytes;
        }

        _resultImage = _creator.CreateResultImage();
        VerifyLayout(layout, trackIndexes);
        return (IStream)_resultImage;
    }

    private const int SectorBytes = 2352;

    private void VerifyLayout(CdDaoTrackLayout layout, IReadOnlyList<object> trackIndexes)
    {
        long leadout = StartOfLeadout;
        List<string> problems = [];
        if (leadout != layout.StartOfLeadout)
        {
            problems.Add($"lead-out at LBA {leadout}, planned {layout.StartOfLeadout}");
        }

        for (int index = 0; index < trackIndexes.Count; index++)
        {
            CdDaoTrackReport report = ReadTrack(trackIndexes[index]);
            long plannedLba = layout.TrackStartLba(index);
            if (report.StartingLba != plannedLba || report.SectorCount != layout.TrackSectors[index])
            {
                problems.Add($"track {index + 1} at LBA {report.StartingLba} with {report.SectorCount} sectors, "
                    + $"planned LBA {plannedLba} with {layout.TrackSectors[index]}");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "IMAPI placed the CD tracks differently from the plan (" + string.Join("; ", problems)
                + "). Nothing was written; the picture would not line up.");
        }
    }

    /// <summary>Track facts IMAPI reports after the image is created (IRawCDImageTrackInfo),
    /// read through IDispatch because TrackInfo is a parameterized property.</summary>
    internal CdDaoTrackReport ReadTrack(object trackIndex)
    {
        object creator = _creator;
        object info = creator.GetType().InvokeMember("TrackInfo",
            System.Reflection.BindingFlags.GetProperty, null, creator, [trackIndex])!;
        try
        {
            dynamic track = info;
            return new CdDaoTrackReport(
                Convert.ToInt32(track.TrackNumber),
                Convert.ToInt64(track.StartingLba),
                Convert.ToInt64(track.SectorCount));
        }
        finally
        {
            if (Marshal.IsComObject(info))
            {
                Marshal.ReleaseComObject(info);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_resultImage is not null && Marshal.IsComObject(_resultImage))
        {
            Marshal.FinalReleaseComObject(_resultImage);
        }

        if (Marshal.IsComObject(_creator))
        {
            Marshal.FinalReleaseComObject(_creator);
        }
    }
}

/// <summary>A track as IMAPI's image creator reports it after CreateResultImage.</summary>
internal readonly record struct CdDaoTrackReport(int TrackNumber, long StartingLba, long SectorCount);
