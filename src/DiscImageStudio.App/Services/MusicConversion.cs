using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DiscImageStudio.Cd.Music;

namespace DiscImageStudio.Services;

/// <summary>Windows cannot convert the file (no Media Foundation, or an unreadable format); the
/// message is ready to show.</summary>
internal sealed class MusicConversionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Prepares songs for a picture + music CD: CD-format WAVs (44.1 kHz, 16-bit, stereo PCM) are used
/// as they are; anything else Windows can play (MP3, M4A/AAC, WMA, FLAC, other WAVs) is converted
/// with Windows Media Foundation into the app cache, keyed by the file's path, size and time, so a
/// song is converted once per session.
/// </summary>
internal static class MusicConversion
{
    internal const string FileFilter = "*.mp3;*.m4a;*.aac;*.wma;*.flac;*.wav";

    internal static CdMusicSource Prepare(string sourcePath, string? title, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
        {
            throw new MusicConversionException($"{Path.GetFileName(fullPath)} was not found.");
        }

        string songTitle = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fullPath) : title.Trim();
        if (Path.GetExtension(fullPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return CdMusicSource.FromWave(fullPath, songTitle);
            }
            catch (InvalidDataException)
            {
                // Not CD format: Media Foundation converts it below.
            }
        }

        FileInfo info = new(fullPath);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{fullPath.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")))[..24].ToLowerInvariant();
        string target = TempFiles.GetCacheFilePath($"music-{key}.wav");
        if (File.Exists(target))
        {
            try
            {
                return CdMusicSource.FromWave(target, songTitle);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                TempFiles.TryDeleteTemporaryFile(target);
            }
        }

        string partial = TempFiles.GetCacheFilePath($"music-{key}.partial");
        try
        {
            MediaFoundationAudio.ConvertToCdWave(fullPath, partial, cancellationToken);
            File.Move(partial, target, overwrite: true);
        }
        finally
        {
            TempFiles.TryDeleteTemporaryFile(partial);
        }

        return CdMusicSource.FromWave(target, songTitle);
    }
}

/// <summary>
/// The smallest Media Foundation path from a media file to CD audio: a source reader whose audio
/// output type is 44.1 kHz / 16-bit / stereo PCM. Since Windows 8 the source reader inserts the
/// decoder, resampler and channel mixer itself.
/// </summary>
internal static class MediaFoundationAudio
{
    private const int MfVersion = 0x00020070;
    private const int MfStartupLite = 1;
    private const int AllStreams = unchecked((int)0xFFFFFFFE);
    private const int FirstAudioStream = unchecked((int)0xFFFFFFFD);
    private const int ReaderError = 0x1;
    private const int ReaderEndOfStream = 0x2;
    private const int ReaderCurrentMediaTypeChanged = 0x20;
    private const int UnsupportedByteStream = unchecked((int)0xC00D36C4);
    private const int InvalidMediaType = unchecked((int)0xC00D36B4);
    private const int NoAudioStream = unchecked((int)0xC00D36B3);

    internal static void ConvertToCdWave(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        string name = Path.GetFileName(sourcePath);
        int startup;
        try
        {
            startup = MFStartup(MfVersion, MfStartupLite);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new MusicConversionException(Unavailable, exception);
        }

        if (startup < 0)
        {
            throw new MusicConversionException(Unavailable, Marshal.GetExceptionForHR(startup));
        }

        try
        {
            IMFSourceReader reader = Create(name, sourcePath);
            try
            {
                Configure(name, reader);
                using FileStream output = new(targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.SequentialScan);
                output.Write(new byte[44]);
                long audioBytes = CopySamples(name, reader, output, cancellationToken);
                if (audioBytes == 0)
                {
                    throw new MusicConversionException($"{name}: Windows decoded no audio from this file.");
                }

                if (audioBytes > uint.MaxValue - 36L)
                {
                    throw new MusicConversionException($"{name} is too long for a WAV file.");
                }

                output.Position = 0;
                WriteHeader(output, audioBytes);
            }
            finally
            {
                Marshal.ReleaseComObject(reader);
            }
        }
        finally
        {
            _ = MFShutdown();
        }
    }

    private static string Unavailable =>
        "Windows Media Foundation is not available, so this file cannot be converted (Windows N editions need "
        + "the Media Feature Pack). CD-format WAV files (44.1 kHz, 16-bit, stereo) work without it.";

    private static IMFSourceReader Create(string name, string sourcePath)
    {
        int result = MFCreateSourceReaderFromURL(sourcePath, IntPtr.Zero, out IMFSourceReader? reader);
        if (result < 0 || reader is null)
        {
            throw new MusicConversionException(result == UnsupportedByteStream
                ? $"{name}: Windows cannot read this file type."
                : $"{name}: Windows cannot open this file (0x{result:X8}).", Marshal.GetExceptionForHR(result));
        }

        return reader;
    }

    private static void Configure(string name, IMFSourceReader reader)
    {
        Check(name, reader.SetStreamSelection(AllStreams, false), "select streams");
        int selected = reader.SetStreamSelection(FirstAudioStream, true);
        if (selected < 0)
        {
            throw new MusicConversionException(selected == NoAudioStream
                ? $"{name} has no audio." : $"{name}: Windows cannot select its audio (0x{selected:X8}).");
        }

        Check(name, MFCreateMediaType(out IntPtr mediaType), "create the CD audio type");
        try
        {
            WaveFormatEx format = new()
            {
                FormatTag = 1,
                Channels = 2,
                SamplesPerSecond = 44100,
                AverageBytesPerSecond = 176400,
                BlockAlign = 4,
                BitsPerSample = 16,
                ExtraSize = 0,
            };
            Check(name, MFInitMediaTypeFromWaveFormatEx(mediaType, ref format, Marshal.SizeOf<WaveFormatEx>()),
                "describe CD audio");
            int set = reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, mediaType);
            if (set < 0)
            {
                throw new MusicConversionException(set == InvalidMediaType
                    ? $"{name}: Windows cannot convert this audio to CD format (44.1 kHz, 16-bit, stereo)."
                    : $"{name}: Windows cannot convert this audio (0x{set:X8}).", Marshal.GetExceptionForHR(set));
            }
        }
        finally
        {
            Marshal.Release(mediaType);
        }
    }

    private static long CopySamples(string name, IMFSourceReader reader, Stream output, CancellationToken cancellationToken)
    {
        long written = 0;
        byte[] chunk = [];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(name, reader.ReadSample(FirstAudioStream, 0, out _, out int flags, out _, out IMFSample? sample), "decode");
            try
            {
                if ((flags & ReaderError) != 0)
                {
                    throw new MusicConversionException($"{name}: Windows stopped decoding with an error.");
                }

                if ((flags & ReaderCurrentMediaTypeChanged) != 0)
                {
                    throw new MusicConversionException($"{name}: the audio format changed while decoding.");
                }

                if (sample is not null)
                {
                    Check(name, sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer), "read decoded audio");
                    try
                    {
                        Check(name, buffer.Lock(out IntPtr data, out _, out int length), "read decoded audio");
                        try
                        {
                            if (chunk.Length < length) chunk = new byte[length];
                            Marshal.Copy(data, chunk, 0, length);
                            output.Write(chunk, 0, length);
                            written += length;
                        }
                        finally
                        {
                            buffer.Unlock();
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(buffer);
                    }
                }
            }
            finally
            {
                if (sample is not null) Marshal.ReleaseComObject(sample);
            }

            if ((flags & ReaderEndOfStream) != 0)
            {
                return written - written % 4;
            }
        }
    }

    private static void WriteHeader(Stream output, long audioBytes)
    {
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(audioBytes + 36)));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 176400);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], checked((uint)audioBytes));
        output.Write(header);
        // A trailing odd byte from a truncated last sample would make the data chunk odd-sized.
        output.SetLength(44 + audioBytes);
    }

    private static void Check(string name, int result, string action)
    {
        if (result < 0)
        {
            throw new MusicConversionException($"{name}: Windows could not {action} (0x{result:X8}).",
                Marshal.GetExceptionForHR(result));
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IntPtr mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFInitMediaTypeFromWaveFormatEx(IntPtr mediaType, ref WaveFormatEx format, int size);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes,
        [MarshalAs(UnmanagedType.Interface)] out IMFSourceReader? reader);

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        [PreserveSig] int SetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [PreserveSig] int GetNativeMediaType(int streamIndex, int mediaTypeIndex, out IntPtr mediaType);
        [PreserveSig] int GetCurrentMediaType(int streamIndex, out IntPtr mediaType);
        [PreserveSig] int SetCurrentMediaType(int streamIndex, IntPtr reserved, IntPtr mediaType);
        [PreserveSig] int SetCurrentPosition(ref Guid timeFormat, IntPtr position);
        [PreserveSig] int ReadSample(int streamIndex, int controlFlags, out int actualStreamIndex, out int streamFlags,
            out long timestamp, [MarshalAs(UnmanagedType.Interface)] out IMFSample? sample);
    }

    /// <summary>IMFSample: the 30 IMFAttributes methods come first in its vtable.</summary>
    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        [PreserveSig] int GetItem(); [PreserveSig] int GetItemType(); [PreserveSig] int CompareItem();
        [PreserveSig] int Compare(); [PreserveSig] int GetUINT32(); [PreserveSig] int GetUINT64();
        [PreserveSig] int GetDouble(); [PreserveSig] int GetGUID(); [PreserveSig] int GetStringLength();
        [PreserveSig] int GetString(); [PreserveSig] int GetAllocatedString(); [PreserveSig] int GetBlobSize();
        [PreserveSig] int GetBlob(); [PreserveSig] int GetAllocatedBlob(); [PreserveSig] int GetUnknown();
        [PreserveSig] int SetItem(); [PreserveSig] int DeleteItem(); [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(); [PreserveSig] int SetUINT64(); [PreserveSig] int SetDouble();
        [PreserveSig] int SetGUID(); [PreserveSig] int SetString(); [PreserveSig] int SetBlob();
        [PreserveSig] int SetUnknown(); [PreserveSig] int LockStore(); [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(); [PreserveSig] int GetItemByIndex(); [PreserveSig] int CopyAllItems();
        [PreserveSig] int GetSampleFlags(out int flags);
        [PreserveSig] int SetSampleFlags(int flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out int count);
        [PreserveSig] int GetBufferByIndex(int index, out IntPtr buffer);
        [PreserveSig] int ConvertToContiguousBuffer([MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maximumLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
        [PreserveSig] int GetMaxLength(out int length);
    }
}
