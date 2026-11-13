using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DiscImageStudio.Burning;

/// <summary>
/// Multi-track CD audio images for picture + music discs, built by native IMAPI only (no recorder,
/// no DiscMaster, no write interface). The Q subchannel of the finished image is the ground truth
/// for where every content sector lands: its absolute time gives each section's LBA, its track and
/// index numbers give the track structure players see. A picture only lines up when its bytes keep
/// their planned LBAs, so every sector is compared byte for byte.
/// </summary>
internal static class CdMusicImapiLayoutTests
{
    private const int AudioBytes = 2352;
    private const int RawBytes = AudioBytes + 96;
    private const int StgEInvalidFunction = unchecked((int)0x80030001);

    internal static void Run()
    {
        Probe();
        TestSliceOrder();
        TestRequestValidation();
        foreach (int sectorType in new[] { CdRawSectorType.SubcodeIsCooked, CdRawSectorType.SubcodeIsRaw })
        {
            // Music on the inner ring: two songs, then the picture as the final track.
            Check(new CdDaoTrackLayout(0, [300, 451, 600]), sectorType, "inner-ring music");
            // Music on the outer ring: 150 silent sectors, a 600-sector picture and a 75-sector
            // guard form track 1's hidden pregap; the songs are the tracks.
            Check(new CdDaoTrackLayout(150 + 600 + 75, [300, 451]), sectorType, "outer-ring music");
        }

        Console.WriteLine("cd-music-imapi-layout: passed (multi-track and hidden-pregap DAO images keep every "
            + "content sector at its planned LBA, Q track/index/time follow the layout, cooked R-W and raw P-W)");
    }

    /// <summary>Facts about the installed IMAPI, printed for the CI log before anything is asserted:
    /// the gapless default, the track index base, and which special pregaps and call orders it accepts.</summary>
    private static void Probe()
    {
        foreach (bool pregapFirst in new[] { true, false })
        {
            foreach (long pregap in new long[] { 0, 150, 151, 825, 45_000 })
            {
                if (pregap == 0 && !pregapFirst) continue;
                string name = pregap == 0 ? "no special pregap" : $"special pregap {pregap} ({(pregapFirst ? "before" : "after")} AddTrack)";
                object? creator = null;
                try
                {
                    creator = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftRawCDImageCreator", true)!)!;
                    dynamic native = creator;
                    bool gaplessDisabled = Convert.ToBoolean(native.DisableGaplessAudio);
                    native.ResultingImageType = CdRawSectorType.SubcodeIsCooked;
                    if (pregap > 0 && pregapFirst) native.AddSpecialPregap(new VirtualContent(pregap * AudioBytes));
                    object first = native.AddTrack(0, new VirtualContent(300L * AudioBytes));
                    if (pregap > 0 && !pregapFirst) native.AddSpecialPregap(new VirtualContent(pregap * AudioBytes));
                    object second = native.AddTrack(0, new VirtualContent(450L * AudioBytes));
                    object image = native.CreateResultImage();
                    string tracks = string.Join(", ", new[] { first, second }.Select(index =>
                    {
                        object info = creator.GetType().InvokeMember("TrackInfo",
                            System.Reflection.BindingFlags.GetProperty, null, creator, [index])!;
                        dynamic track = info;
                        string text = $"index {index}: track {track.TrackNumber} LBA {track.StartingLba} sectors {track.SectorCount}";
                        Marshal.ReleaseComObject(info);
                        return text;
                    }));
                    Console.WriteLine($"cd-music-imapi-probe: {name}: DisableGaplessAudio default={gaplessDisabled}; "
                        + $"lead-out {native.StartOfLeadout}; {tracks}");
                    if (Marshal.IsComObject(image)) Marshal.FinalReleaseComObject(image);
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"cd-music-imapi-probe: {name}: refused, {exception.GetType().Name} "
                        + $"0x{exception.HResult:X8}: {exception.Message}");
                }
                finally
                {
                    if (creator is not null && Marshal.IsComObject(creator)) Marshal.FinalReleaseComObject(creator);
                }
            }
        }
    }

    private static void TestSliceOrder()
    {
        byte[] content = Content(new CdDaoTrackLayout(0, [300, 300]), 0);
        using GeneratedContentComStream stream = new(content.Length, (output, _) => output.Write(content), null,
            CancellationToken.None);
        stream.WaitUntilPrebuffered(CancellationToken.None);
        CdTrackSliceStream.SharedSource source = new(stream);
        CdTrackSliceStream first = new(source, 0, 300L * AudioBytes);
        CdTrackSliceStream second = new(source, 300L * AudioBytes, 300L * AudioBytes);
        first.Stat(out STATSTG stat, 1);
        Require(stat.cbSize == 300L * AudioBytes, "a track window reports its own length");
        first.Seek(0, 2, IntPtr.Zero);
        first.Seek(0, 0, IntPtr.Zero);
        byte[] buffer = new byte[AudioBytes];
        COMException? early = null;
        try { second.Read(buffer, buffer.Length, IntPtr.Zero); }
        catch (COMException exception) { early = exception; }
        Require(early?.HResult == StgEInvalidFunction && early.Message.Contains("out of order", StringComparison.Ordinal),
            "reading a later track before the earlier one is reported, not silently misplaced");
        first.Read(buffer, buffer.Length, IntPtr.Zero);
        Require(buffer.AsSpan().SequenceEqual(content.AsSpan(0, AudioBytes)), "the first window starts at the content start");
    }

    private static void TestRequestValidation()
    {
        static void Produce(Stream output, CancellationToken token) { }
        CdDaoTrackLayout layout = new(0, [300, 300]);
        new OpticalBurnRequest("device", OpticalBurnMediaKind.CdAudio, layout.ContentBytes, Produce,
            CdTrackLayout: layout).Validate();
        RequireThrows<ArgumentException>(() => new OpticalBurnRequest("device", OpticalBurnMediaKind.CdAudio,
            layout.ContentBytes + AudioBytes, Produce, CdTrackLayout: layout).Validate(),
            "a layout must cover exactly the content");
        RequireThrows<ArgumentException>(() => new OpticalBurnRequest("device", OpticalBurnMediaKind.CdData,
            600L * 2048, Produce, CdTrackLayout: layout).Validate(), "data discs cannot carry an audio track layout");
        RequireThrows<ArgumentOutOfRangeException>(() => new CdDaoTrackLayout(0, [300, 299]).Validate(),
            "tracks shorter than 4 seconds are rejected");
        RequireThrows<ArgumentOutOfRangeException>(() => new CdDaoTrackLayout(149, [300]).Validate(),
            "a special pregap shorter than the standard 2 seconds is rejected");
        using CdDataRawImageSession data = new();
        RequireThrows<NotSupportedException>(() => ((ICdRawImageSession)data).CreateImage(
            new VirtualContent(layout.ContentSectors * 2048), CdRawSectorType.SubcodeIsCooked, layout),
            "the CD data image session refuses audio track layouts");
    }

    private static void Check(CdDaoTrackLayout layout, int sectorType, string name)
    {
        string context = $"{name}, sector type {sectorType}";
        byte[] content = Content(layout, sectorType);
        using GeneratedContentComStream stream = new(content.Length, (output, _) => output.Write(content), null,
            CancellationToken.None);
        stream.WaitUntilPrebuffered(CancellationToken.None);
        using DynamicCdRawImageSession session = new();
        IStream image;
        try
        {
            image = ((ICdRawImageSession)session).CreateImage(stream, sectorType, layout);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"CD music layout ({context}): IMAPI image creation failed: "
                + exception.Message, exception);
        }

        Require(session.StartOfLeadout == layout.StartOfLeadout, $"{context}: lead-out at LBA {layout.StartOfLeadout}");
        image.Stat(out STATSTG stat, 1);
        Require(stat.cbSize % RawBytes == 0, $"{context}: complete 2448-byte sections");
        long sections = stat.cbSize / RawBytes;
        byte[] raw = new byte[RawBytes];
        long? sectionOfLbaZero = null;
        long matchedContent = 0, checkedQ = 0;
        long? leadoutLba = null;
        // Sections whose Q is not an ADR1 timing packet are kept until the first program-area
        // timing fixes the section-to-LBA mapping (ADR1 is at least nine in ten packets).
        List<(long Section, byte[] Raw, byte[] Q)> pending = [];

        void Process(long section, byte[] main, byte[] q)
        {
            long current = section - sectionOfLbaZero!.Value;
            if (current < -150)
            {
                return; // lead-in
            }

            bool adr1 = (q[0] & 15) == 1;
            Require(!adr1 || q[1] != 0x00, $"{context}: no lead-in packet follows the program area start (LBA {current})");
            long contentSector = current - layout.ContentStartLba;
            if (contentSector >= 0 && contentSector < layout.ContentSectors)
            {
                Require(main.AsSpan(0, AudioBytes).SequenceEqual(content.AsSpan(checked((int)(contentSector * AudioBytes)), AudioBytes)),
                    $"{context}: content sector {contentSector} lands at LBA {current} byte for byte");
                matchedContent++;
            }
            else if (current < layout.StartOfLeadout)
            {
                Require(main.AsSpan(0, AudioBytes).IndexOfAnyExcept((byte)0) < 0,
                    $"{context}: LBA {current} outside the content is IMAPI's silent pregap");
            }

            if (adr1)
            {
                CheckQ(layout, q, current, context);
                checkedQ++;
                if (q[1] == 0xAA) leadoutLba ??= current;
            }
        }

        IntPtr count = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            for (long section = 0; section < sections; section++)
            {
                Marshal.WriteInt32(count, -1);
                image.Read(raw, raw.Length, count);
                Require(Marshal.ReadInt32(count) == RawBytes, $"{context}: section {section} reads completely in one pass");
                byte[] q = DecodeQ(raw);
                Require(ValidCrc(q), $"{context}: section {section} Q CRC is valid");
                bool adr1 = (q[0] & 15) == 1;
                if (adr1 && q[1] != 0x00)
                {
                    long mapped = section - (DecodeMsf(q.AsSpan(7, 3)) - 150);
                    Require(sectionOfLbaZero is null || sectionOfLbaZero == mapped,
                        $"{context}: Q absolute time advances one frame per section (section {section})");
                    sectionOfLbaZero ??= mapped;
                }

                if (sectionOfLbaZero is null)
                {
                    if (!(adr1 && q[1] == 0x00))
                    {
                        pending.Add((section, raw.ToArray(), q));
                        if (pending.Count > 32) pending.RemoveAt(0);
                    }

                    continue;
                }

                foreach ((long Section, byte[] Raw, byte[] Q) early in pending) Process(early.Section, early.Raw, early.Q);
                pending.Clear();
                Process(section, raw, q);
            }

            Marshal.WriteInt32(count, -1);
            image.Read(raw, 1, count);
            Require(Marshal.ReadInt32(count) == 0, $"{context}: the image ends at its declared length");
        }
        finally
        {
            Marshal.FreeCoTaskMem(count);
            if (Marshal.IsComObject(image)) Marshal.FinalReleaseComObject(image);
        }

        Require(sectionOfLbaZero is not null, $"{context}: the image has program-area Q timing");
        Require(matchedContent == layout.ContentSectors,
            $"{context}: all {layout.ContentSectors} content sectors are in the image (found {matchedContent})");
        Require(leadoutLba == layout.StartOfLeadout, $"{context}: Q lead-out starts at LBA {layout.StartOfLeadout} (found {leadoutLba})");
        Require(checkedQ > layout.ContentSectors / 2, $"{context}: most sections carry ADR1 timing");
    }

    /// <summary>Expected track, index and relative time of an ADR1 program-area packet.</summary>
    private static void CheckQ(CdDaoTrackLayout layout, byte[] q, long lba, string context)
    {
        int track;
        int index;
        long relative;
        if (lba >= layout.StartOfLeadout)
        {
            Require(q[1] == 0xAA && q[2] == 0x01 && DecodeMsf(q.AsSpan(3, 3)) == lba - layout.StartOfLeadout,
                $"{context}: LBA {lba} is lead-out");
            return;
        }

        if (lba < layout.FirstTrackLba)
        {
            // Native IMAPI counts the pregap down to 1 at the last sector before index 01.
            track = 1;
            index = 0;
            relative = layout.FirstTrackLba - lba;
        }
        else
        {
            track = layout.TrackSectors.Count;
            while (layout.TrackStartLba(track - 1) > lba) track--;
            index = 1;
            relative = lba - layout.TrackStartLba(track - 1);
        }

        Require(q[1] == Bcd(track) && q[2] == Bcd(index),
            $"{context}: LBA {lba} is track {track} index {index:00} (Q says track {q[1]:X2} index {q[2]:X2})");
        Require(DecodeMsf(q.AsSpan(3, 3)) == relative,
            $"{context}: LBA {lba} relative time {relative} (Q says {DecodeMsf(q.AsSpan(3, 3))})");
    }

    /// <summary>Every content sector is unique: its index, the sector type and a seeded
    /// pseudo-random body with asymmetric samples (so byte swaps would also be detected).</summary>
    private static byte[] Content(CdDaoTrackLayout layout, int sectorType)
    {
        byte[] content = new byte[layout.ContentBytes];
        for (long sector = 0; sector < layout.ContentSectors; sector++)
        {
            Span<byte> bytes = content.AsSpan(checked((int)(sector * AudioBytes)), AudioBytes);
            new Random(unchecked((int)(sector * 7919 + sectorType))).NextBytes(bytes);
            BitConverter.TryWriteBytes(bytes, sector);
            for (int offset = 0; offset < bytes.Length; offset += 2)
                if (bytes[offset] == bytes[offset + 1]) bytes[offset + 1] ^= 0xFF;
        }

        return content;
    }

    private static byte Bcd(int value) => (byte)(((value / 10) << 4) | (value % 10));

    private static long DecodeMsf(ReadOnlySpan<byte> msf)
    {
        static int Digits(byte value) => (value >> 4) * 10 + (value & 15);
        return (Digits(msf[0]) * 60L + Digits(msf[1])) * 75 + Digits(msf[2]);
    }

    private static byte[] DecodeQ(byte[] raw)
    {
        byte[] q = new byte[12];
        for (int bit = 0; bit < 96; bit++)
            q[bit / 8] |= (byte)(((raw[AudioBytes + bit] >> 6) & 1) << (7 - bit % 8));
        return q;
    }

    private static bool ValidCrc(byte[] q)
    {
        uint crc = 0;
        for (int index = 0; index < 10; index++)
        {
            crc ^= (uint)q[index] << 8;
            for (int bit = 0; bit < 8; bit++)
                crc = ((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021u : 0)) & 0xFFFF;
        }

        return (ushort)~crc == (q[10] << 8 | q[11]);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD music layout: " + message);
    }

    private static void RequireThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("CD music layout: " + message);
    }

    /// <summary>Random-access zero content for probes that never read the image.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class VirtualContent(long length) : IStream
    {
        private long _position;
        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int read = (int)Math.Min(cb, length - _position);
            Array.Clear(pv, 0, read);
            _position += read;
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, read);
        }
        public void Seek(long offset, int origin, IntPtr newPosition)
        {
            _position = origin switch { 0 => offset, 1 => _position + offset, _ => length + offset };
            if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, _position);
        }
        public void Stat(out STATSTG stat, int flags) => stat = new() { type = 2, cbSize = length };
        public void Write(byte[] pv, int cb, IntPtr written) => throw new NotSupportedException();
        public void SetSize(long size) => throw new NotSupportedException();
        public void CopyTo(IStream stream, long cb, IntPtr read, IntPtr written) => throw new NotSupportedException();
        public void Commit(int flags) { }
        public void Revert() => throw new NotSupportedException();
        public void LockRegion(long offset, long cb, int type) => throw new NotSupportedException();
        public void UnlockRegion(long offset, long cb, int type) => throw new NotSupportedException();
        public void Clone(out IStream stream) => throw new NotSupportedException();
    }
}
