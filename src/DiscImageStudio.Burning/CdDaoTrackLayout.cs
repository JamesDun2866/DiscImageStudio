namespace DiscImageStudio.Burning;

/// <summary>
/// Track structure of a multi-track CD audio DAO burn, counted in 2352-byte sectors of the
/// content stream, which holds the tracks back to back. The content always starts at LBA 0:
/// IMAPI writes the standard silent 2-second pregap (LBA -150…-1) itself, and the tracks follow
/// each other without gaps.
/// <para>A special pregap continues track 1's index 00 from LBA 0: the first
/// <see cref="SpecialPregapSectors"/> content sectors are hidden from players, which start at track
/// 1's index 01, LBA <c>SpecialPregapSectors</c> ("hidden track one audio"). Native IMAPI accepts
/// it only before the first track is added (verified on the Windows CI runner).</para>
/// A picture on the disc only lines up when every byte keeps its planned LBA, so the image
/// session compares these numbers with what IMAPI built before anything is written.
/// </summary>
public sealed record CdDaoTrackLayout
{
    public const int MaximumTracks = 99;

    /// <summary>ECMA-130 minimum track length: 4 seconds.</summary>
    public const long MinimumTrackSectors = 300;

    /// <summary>The standard first-track pregap: 2 seconds.</summary>
    public const long StandardPregapSectors = 150;

    public CdDaoTrackLayout(long specialPregapSectors, IReadOnlyList<long> trackSectors)
    {
        ArgumentNullException.ThrowIfNull(trackSectors);
        SpecialPregapSectors = specialPregapSectors;
        TrackSectors = trackSectors.ToArray();
    }

    /// <summary>Sectors of track 1's index 00 supplied at the start of the content, after IMAPI's
    /// own silent pregap; 0 for none.</summary>
    public long SpecialPregapSectors { get; }

    public IReadOnlyList<long> TrackSectors { get; }

    public long FirstTrackLba => SpecialPregapSectors;

    public long ContentSectors => SpecialPregapSectors + TrackSectors.Sum();

    public long ContentBytes => checked(ContentSectors * 2352);

    public long StartOfLeadout => FirstTrackLba + TrackSectors.Sum();

    /// <summary>LBA of the index 01 of the zero-based track <paramref name="index"/>.</summary>
    public long TrackStartLba(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, TrackSectors.Count);
        long lba = FirstTrackLba;
        for (int track = 0; track < index; track++)
        {
            lba += TrackSectors[track];
        }

        return lba;
    }

    public void Validate()
    {
        if (TrackSectors.Count is 0 or > MaximumTracks)
        {
            throw new ArgumentOutOfRangeException(nameof(TrackSectors),
                $"A CD holds 1–{MaximumTracks} tracks; the layout has {TrackSectors.Count}.");
        }

        for (int index = 0; index < TrackSectors.Count; index++)
        {
            if (TrackSectors[index] < MinimumTrackSectors)
            {
                throw new ArgumentOutOfRangeException(nameof(TrackSectors),
                    $"Track {index + 1} has {TrackSectors[index]} sectors; CD tracks need at least "
                    + $"{MinimumTrackSectors} (4 seconds).");
            }
        }

        if (SpecialPregapSectors != 0 && SpecialPregapSectors < StandardPregapSectors)
        {
            throw new ArgumentOutOfRangeException(nameof(SpecialPregapSectors),
                $"A special pregap needs at least {StandardPregapSectors} sectors (2 seconds).");
        }

        _ = ContentBytes;
    }

    public override string ToString()
        => $"pregap={SpecialPregapSectors}, tracks=[{string.Join(", ", TrackSectors)}], "
            + $"track1Lba={FirstTrackLba}, leadout={StartOfLeadout}";
}
