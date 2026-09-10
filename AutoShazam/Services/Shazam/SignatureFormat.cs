using System.IO;

namespace AutoShazam.Services.Shazam;

/// <summary>
/// Binary encoding for Shazam's "audio/vnd.shazam.sig" signature format.
/// Ported from the reverse-engineered format used by Shazam's own mobile apps
/// (field layout: 48-byte header + CRC32 + type-length-value peak lists per frequency band).
/// </summary>
internal enum FrequencyBand
{
    Band250To520 = 0,
    Band520To1450 = 1,
    Band1450To3500 = 2,
    Band3500To5500 = 3,
}

internal sealed class FrequencyPeak
{
    public FrequencyPeak(int fftPassNumber, int peakMagnitude, int correctedPeakFrequencyBin, int sampleRateHz)
    {
        FftPassNumber = fftPassNumber;
        PeakMagnitude = peakMagnitude;
        CorrectedPeakFrequencyBin = correctedPeakFrequencyBin;
        SampleRateHz = sampleRateHz;
    }

    public int FftPassNumber { get; }
    public int PeakMagnitude { get; }
    public int CorrectedPeakFrequencyBin { get; }
    public int SampleRateHz { get; }

    public double GetSeconds() => FftPassNumber * 128.0 / SampleRateHz;
}

internal sealed class DecodedMessage
{
    private const uint HeaderMagic1 = 0xCAFE2580;
    private const uint HeaderMagic2 = 0x94119C00;
    private const uint HeaderMagic3 = (15 << 19) + 0x40000;
    private const string DataUriPrefix = "data:audio/vnd.shazam.sig;base64,";

    private static readonly Dictionary<int, uint> SampleRateToShiftedId = new()
    {
        [8000] = 1u << 27,
        [11025] = 2u << 27,
        [16000] = 3u << 27,
        [32000] = 4u << 27,
        [44100] = 5u << 27,
        [48000] = 6u << 27,
    };

    public int SampleRateHz { get; set; } = 16000;

    public int NumberSamples { get; set; }

    public Dictionary<FrequencyBand, List<FrequencyPeak>> FrequencyBandToSoundPeaks { get; set; } = new();

    public byte[] EncodeToBinary()
    {
        using var contents = new MemoryStream();
        foreach (var band in FrequencyBandToSoundPeaks.Keys.OrderBy(b => (int)b))
        {
            var peaks = FrequencyBandToSoundPeaks[band];
            using var peaksBuf = new MemoryStream();
            int fftPassNumber = 0;

            foreach (var peak in peaks)
            {
                if (peak.FftPassNumber < fftPassNumber)
                {
                    throw new InvalidOperationException("Peaks must be sorted by fft pass number.");
                }

                if (peak.FftPassNumber - fftPassNumber >= 255)
                {
                    peaksBuf.WriteByte(0xFF);
                    WriteUInt32Le(peaksBuf, (uint)peak.FftPassNumber);
                    fftPassNumber = peak.FftPassNumber;
                }

                peaksBuf.WriteByte((byte)(peak.FftPassNumber - fftPassNumber));
                WriteUInt16Le(peaksBuf, (ushort)peak.PeakMagnitude);
                WriteUInt16Le(peaksBuf, (ushort)peak.CorrectedPeakFrequencyBin);

                fftPassNumber = peak.FftPassNumber;
            }

            var peaksBytes = peaksBuf.ToArray();
            WriteUInt32Le(contents, 0x60030040u + (uint)band);
            WriteUInt32Le(contents, (uint)peaksBytes.Length);
            contents.Write(peaksBytes, 0, peaksBytes.Length);
            int padding = (4 - (peaksBytes.Length % 4)) % 4;
            for (int i = 0; i < padding; i++)
            {
                contents.WriteByte(0);
            }
        }

        var contentsBytes = contents.ToArray();
        uint sizeMinusHeader = (uint)contentsBytes.Length + 8;

        using var buf = new MemoryStream();
        WriteUInt32Le(buf, HeaderMagic1);
        WriteUInt32Le(buf, 0u); // crc32 placeholder, filled below
        WriteUInt32Le(buf, sizeMinusHeader);
        WriteUInt32Le(buf, HeaderMagic2);
        WriteUInt32Le(buf, 0u); // void1[0..2]
        WriteUInt32Le(buf, 0u);
        WriteUInt32Le(buf, 0u);
        WriteUInt32Le(buf, SampleRateToShiftedId[SampleRateHz]);
        WriteUInt32Le(buf, 0u); // void2[0..1]
        WriteUInt32Le(buf, 0u);
        WriteUInt32Le(buf, (uint)(NumberSamples + SampleRateHz * 0.24));
        WriteUInt32Le(buf, HeaderMagic3);

        WriteUInt32Le(buf, 0x40000000u);
        WriteUInt32Le(buf, (uint)contentsBytes.Length + 8);
        buf.Write(contentsBytes, 0, contentsBytes.Length);

        var full = buf.ToArray();
        uint crc = Crc32.Compute(full.AsSpan(8));

        // Rewrite crc32 field (bytes 4..7)
        full[4] = (byte)(crc & 0xFF);
        full[5] = (byte)((crc >> 8) & 0xFF);
        full[6] = (byte)((crc >> 16) & 0xFF);
        full[7] = (byte)((crc >> 24) & 0xFF);

        return full;
    }

    public string EncodeToUri() => DataUriPrefix + Convert.ToBase64String(EncodeToBinary());

    private static void WriteUInt32Le(Stream s, uint value)
    {
        s.WriteByte((byte)(value & 0xFF));
        s.WriteByte((byte)((value >> 8) & 0xFF));
        s.WriteByte((byte)((value >> 16) & 0xFF));
        s.WriteByte((byte)((value >> 24) & 0xFF));
    }

    private static void WriteUInt16Le(Stream s, ushort value)
    {
        s.WriteByte((byte)(value & 0xFF));
        s.WriteByte((byte)((value >> 8) & 0xFF));
    }
}
