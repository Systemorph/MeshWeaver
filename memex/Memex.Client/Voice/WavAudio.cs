using System.Buffers.Binary;

namespace Memex.Client.Voice;

/// <summary>
/// Minimal WAV (PCM16) reader → 16 kHz mono float samples, the format Whisper expects.
/// Downmixes stereo; cheap linear-resamples to 16 kHz if needed.
/// </summary>
public static class WavAudio
{
    public static float[] ReadPcm16AsMono16k(Stream stream)
    {
        using var br = new BinaryReader(stream);

        Span<byte> id = stackalloc byte[4];
        ReadExact(br, id);
        if (!id.SequenceEqual("RIFF"u8)) throw new InvalidDataException("Not a RIFF/WAV stream.");
        br.ReadInt32(); // file size
        ReadExact(br, id);
        if (!id.SequenceEqual("WAVE"u8)) throw new InvalidDataException("Not a WAVE stream.");

        short channels = 1, bitsPerSample = 16;
        int sampleRate = 16000;
        byte[]? data = null;

        while (TryReadExact(br, id))
        {
            int chunkSize = br.ReadInt32();
            if (id.SequenceEqual("fmt "u8))
            {
                var fmt = br.ReadBytes(chunkSize);
                short audioFormat = BinaryPrimitives.ReadInt16LittleEndian(fmt.AsSpan(0));
                channels = BinaryPrimitives.ReadInt16LittleEndian(fmt.AsSpan(2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(fmt.AsSpan(14));
                if (audioFormat != 1) throw new NotSupportedException($"Only PCM WAV supported (got format {audioFormat}).");
                if (bitsPerSample != 16) throw new NotSupportedException($"Only 16-bit PCM supported (got {bitsPerSample}).");
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = br.ReadBytes(chunkSize);
            }
            else
            {
                br.BaseStream.Seek(chunkSize, SeekOrigin.Current);
            }
            if ((chunkSize & 1) == 1) br.BaseStream.Seek(1, SeekOrigin.Current); // word-align
        }

        if (data is null) throw new InvalidDataException("WAV had no data chunk.");

        int frameCount = data.Length / 2 / channels;
        var mono = new float[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            int acc = 0;
            for (int c = 0; c < channels; c++)
                acc += BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan((i * channels + c) * 2));
            mono[i] = acc / (float)channels / 32768f;
        }

        return sampleRate == 16000 ? mono : Resample(mono, sampleRate, 16000);
    }

    private static float[] Resample(float[] input, int srcRate, int dstRate)
    {
        if (srcRate == dstRate) return input;
        int outLen = (int)((long)input.Length * dstRate / srcRate);
        var outBuf = new float[outLen];
        double ratio = (double)srcRate / dstRate;
        for (int i = 0; i < outLen; i++)
        {
            double srcPos = i * ratio;
            int i0 = (int)srcPos;
            int i1 = Math.Min(i0 + 1, input.Length - 1);
            float frac = (float)(srcPos - i0);
            outBuf[i] = input[i0] * (1 - frac) + input[i1] * frac;
        }
        return outBuf;
    }

    private static void ReadExact(BinaryReader br, Span<byte> buffer)
    {
        if (!TryReadExact(br, buffer)) throw new EndOfStreamException();
    }

    private static bool TryReadExact(BinaryReader br, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = br.Read(buffer[read..]);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
