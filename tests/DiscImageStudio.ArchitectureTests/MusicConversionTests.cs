using System.Buffers.Binary;
using System.IO;
using DiscImageStudio.Cd.Music;
using DiscImageStudio.Services;

/// <summary>
/// Songs for picture + music CDs: CD-format WAVs pass through unchanged, anything else is converted
/// by Windows Media Foundation to 44.1 kHz / 16-bit / stereo. A 48 kHz mono 1 kHz tone checks
/// resampling (duration and pitch) and the mono-to-stereo mix. Runners without Media Foundation
/// report the conversion part as skipped.
/// </summary>
internal static class MusicConversionTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dis-music-conversion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string cdWave = Path.Combine(directory, "cd format.wav");
            WriteWave(cdWave, 44100, 2, 44100);
            CdMusicSource direct = MusicConversion.Prepare(cdWave, null, CancellationToken.None);
            Require(direct.WavePath == Path.GetFullPath(cdWave) && direct.Title == "cd format"
                && direct.AudioBytes == 44100L * 4, "CD-format WAVs are used as they are");

            string tone = Path.Combine(directory, "tone 48k mono.wav");
            WriteWave(tone, 48000, 1, 96000);
            CdMusicSource converted;
            try
            {
                converted = MusicConversion.Prepare(tone, "Tone", CancellationToken.None);
            }
            catch (MusicConversionException exception) when (exception.Message.StartsWith(
                "Windows Media Foundation is not available", StringComparison.Ordinal))
            {
                Console.WriteLine("music-conversion: CD-format pass-through passed; conversion skipped (no Media Foundation on this runner)");
                return;
            }

            Require(Path.GetFileName(converted.WavePath).StartsWith("music-", StringComparison.Ordinal)
                && converted.Title == "Tone", "converted songs live in the app cache under their own title");
            double seconds = converted.Duration.TotalSeconds;
            Require(Math.Abs(seconds - 2.0) < 0.05, $"conversion keeps the duration (2 s, got {seconds:0.000} s)");
            byte[] pcm = File.ReadAllBytes(converted.WavePath).AsSpan((int)converted.AudioOffset, (int)converted.AudioBytes).ToArray();
            int crossings = 0, mismatched = 0;
            short previous = 0;
            for (int offset = 0; offset < pcm.Length; offset += 4)
            {
                short left = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset));
                short right = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset + 2));
                if (Math.Abs(left - right) > 1) mismatched++;
                if (offset > 0 && (previous < 0) != (left < 0)) crossings++;
                previous = left;
            }

            // A 1 kHz tone crosses zero 2000 times per second.
            Require(Math.Abs(crossings - 4000) < 80, $"conversion keeps the pitch (4000 zero crossings, got {crossings})");
            Require(mismatched == 0, "a mono song plays on both channels");
            CdMusicSource again = MusicConversion.Prepare(tone, "Tone", CancellationToken.None);
            Require(again.WavePath == converted.WavePath, "a song is converted once per session");

            string broken = Path.Combine(directory, "not audio.mp3");
            File.WriteAllText(broken, "this is not an MP3 file");
            bool refused = false;
            try { MusicConversion.Prepare(broken, null, CancellationToken.None); }
            catch (MusicConversionException) { refused = true; }
            Require(refused, "unreadable files are refused with a message");
            Console.WriteLine($"music-conversion: passed (48 kHz mono tone converted to CD audio, {seconds:0.000} s, "
                + $"{crossings} zero crossings; CD-format WAVs pass through)");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A 16-bit PCM WAV with a 1 kHz tone at half scale.</summary>
    private static void WriteWave(string path, int rate, int channels, int frames)
    {
        int bytes = frames * channels * 2;
        byte[] file = new byte[44 + bytes];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), 36 + bytes);
        "WAVEfmt "u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(28), rate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(32), (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(34), 16);
        "data"u8.CopyTo(file.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(40), bytes);
        for (int frame = 0; frame < frames; frame++)
        {
            // Offset by a quarter period so no sample lands exactly on zero.
            short value = (short)(16000 * Math.Sin(2 * Math.PI * 1000 * (frame + 0.25) / rate));
            for (int channel = 0; channel < channels; channel++)
                BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(44 + (frame * channels + channel) * 2), value);
        }

        File.WriteAllBytes(path, file);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Music conversion: " + message);
    }
}
