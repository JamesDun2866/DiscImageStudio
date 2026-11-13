using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace DiscImageStudio.Cd;

internal static class CdWaveFile
{
    internal const int HeaderBytes = 44;

    internal static void ValidateLength(long audioBytes)
    {
        if (audioBytes <= 0 || audioBytes > uint.MaxValue - 36L)
        {
            throw new ArgumentOutOfRangeException(nameof(audioBytes), "Audio exceeds the RIFF/WAV size limit.");
        }
    }

    internal static void WriteHeader(Stream output, long audioBytes)
    {
        ValidateLength(audioBytes);
        Span<byte> header = stackalloc byte[HeaderBytes];
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
    }

    internal static string WriteCue(string wavePath)
    {
        string cuePath = Path.ChangeExtension(wavePath, ".cue");
        File.WriteAllText(cuePath,
            $"FILE \"{Path.GetFileName(wavePath)}\" WAVE\r\n"
            + "  TRACK 01 AUDIO\r\n"
            + "    INDEX 01 00:00:00\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return cuePath;
    }

    /// <summary>
    /// CUE sheet of a picture + music disc: one WAV, one track per song. On the outer-ring layout
    /// track 1 starts with index 00 at 00:00:00, the hidden picture (the convention rippers use for
    /// "hidden track one audio": the file starts at LBA 0, the burner adds the 2-second silence
    /// before it). On the inner-ring layout the picture is the last track, titled as a warning.
    /// </summary>
    internal static string WriteMusicCue(string wavePath, Music.CdMusicLayout layout)
    {
        StringBuilder cue = new();
        cue.Append("REM COMMENT \"Disc Image Studio picture + music CD\"\r\n");
        cue.Append($"FILE \"{Path.GetFileName(wavePath)}\" WAVE\r\n");
        foreach (Music.CdMusicSong song in layout.Songs)
        {
            cue.Append($"  TRACK {song.TrackNumber:00} AUDIO\r\n");
            cue.Append($"    TITLE \"{CueText(song.Source.Title)}\"\r\n");
            if (song.TrackNumber == 1 && layout.SpecialPregapSectors > 0)
            {
                cue.Append("    INDEX 00 00:00:00\r\n");
            }

            cue.Append($"    INDEX 01 {Music.CdMusicLayout.Msf(song.StartLba)}\r\n");
        }

        if (layout.Placement == Music.CdMusicPlacement.InnerRing)
        {
            cue.Append($"  TRACK {layout.Songs.Count + 1:00} AUDIO\r\n");
            cue.Append($"    TITLE \"{Music.CdMusicDiscGenerator.PictureTrackTitle}\"\r\n");
            cue.Append($"    INDEX 01 {Music.CdMusicLayout.Msf(layout.PictureStartLba)}\r\n");
        }

        string cuePath = Path.ChangeExtension(wavePath, ".cue");
        File.WriteAllText(cuePath, cue.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return cuePath;
    }

    /// <summary>CUE strings cannot escape quotes, and CD-Text titles hold 80 characters.</summary>
    internal static string CueText(string text)
    {
        string clean = new(text.Select(character => character == '"' ? '\'' : char.IsControl(character) ? ' ' : character).ToArray());
        clean = clean.Trim();
        return clean.Length <= 80 ? clean : clean[..80];
    }

    internal static (long Offset, long Length) ReadAudioRange(Stream input)
    {
        using BinaryReader reader = new(input, Encoding.ASCII, leaveOpen: true);
        if (input.Length < 12 || reader.ReadUInt32() != 0x46464952)
        {
            throw new InvalidDataException("Expected a RIFF/WAV audio file.");
        }

        long riffEnd = reader.ReadUInt32() + 8L;
        if (reader.ReadUInt32() != 0x45564157 || riffEnd < 12 || riffEnd > input.Length)
        {
            throw new InvalidDataException("The WAV header or declared length is invalid.");
        }

        bool hasFormat = false;
        (long Offset, long Length)? audio = null;
        while (input.Position < riffEnd)
        {
            if (riffEnd - input.Position < 8)
            {
                throw new InvalidDataException("Truncated WAV chunk header.");
            }

            uint chunk = reader.ReadUInt32();
            uint length = reader.ReadUInt32();
            long start = input.Position;
            long next = start + length + (length & 1);
            if (next > riffEnd)
            {
                throw new InvalidDataException("Truncated WAV chunk data.");
            }

            if (chunk == 0x20746D66) // fmt
            {
                if (hasFormat || length < 16
                    || reader.ReadUInt16() != 1
                    || reader.ReadUInt16() != 2
                    || reader.ReadUInt32() != 44100
                    || reader.ReadUInt32() != 176400
                    || reader.ReadUInt16() != 4
                    || reader.ReadUInt16() != 16)
                {
                    throw new InvalidDataException("CD WAV audio must be PCM, 44100 Hz, 16-bit stereo.");
                }

                hasFormat = true;
            }
            else if (chunk == 0x61746164) // data
            {
                if (audio is not null || length == 0 || length % 4 != 0)
                {
                    throw new InvalidDataException("WAV must contain one data chunk with complete stereo samples.");
                }

                audio = (start, length);
            }

            input.Position = next;
        }

        if (!hasFormat || audio is null)
        {
            throw new InvalidDataException("WAV is missing its PCM format or audio data chunk.");
        }

        return audio.Value;
    }
}
