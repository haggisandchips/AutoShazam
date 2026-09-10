using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace AutoShazam.Services.Shazam;

/// <summary>
/// Generates Shazam-compatible audio signatures ("fingerprints") from raw 16kHz mono PCM audio.
/// This is a faithful C# port of the reverse-engineered algorithm used by Shazam's own client
/// apps (spectrogram peak-picking via FFT, with frequency- and time-domain local-maximum spreading).
/// </summary>
internal sealed class SignatureGenerator
{
    private const int RingBufferSamplesSize = 2048;
    private const int FftHistorySize = 256;
    private const int FftBins = 1025;

    private static readonly double[] HanningMatrix = BuildHanningMatrix();

    private static readonly int[] NeighborOffsets = { -10, -7, -4, -3, 1, 2, 5, 8 };
    private static readonly int[] OtherOffsets = { -53, -45, 165, 172, 179, 186, 193, 200, 214, 221, 228, 235, 242, 249 };
    private static readonly int[] FormerFftOffsets = { -1, -3, -6 };

    private readonly List<short> _inputPendingProcessing = new();
    private int _samplesProcessed;

    private readonly int[] _ringBufferOfSamples = new int[RingBufferSamplesSize];
    private int _ringBufferPosition;

    private RingBuffer<double[]> _fftOutputs = NewFftHistory();
    private RingBuffer<double[]> _spreadFftsOutput = NewFftHistory();

    public double MaxTimeSeconds { get; set; } = 3.1;
    public int MaxPeaks { get; set; } = 255;

    private DecodedMessage _nextSignature = NewSignature();

    public int SamplesProcessed => _samplesProcessed;

    private static RingBuffer<double[]> NewFftHistory() => new(FftHistorySize, () => new double[FftBins]);

    private static DecodedMessage NewSignature() => new()
    {
        SampleRateHz = 16000,
        NumberSamples = 0,
        FrequencyBandToSoundPeaks = new Dictionary<FrequencyBand, List<FrequencyPeak>>(),
    };

    private static double[] BuildHanningMatrix()
    {
        // numpy.hanning(2050)[1:-1]: w[n] = 0.5 - 0.5*cos(2*pi*n/(M-1)) for M=2050, n=1..2048
        const int m = 2050;
        var result = new double[2048];
        for (int i = 0; i < 2048; i++)
        {
            int n = i + 1;
            result[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / (m - 1));
        }
        return result;
    }

    public void FeedInput(IEnumerable<short> s16LeMonoSamples)
    {
        _inputPendingProcessing.AddRange(s16LeMonoSamples);
    }

    /// <summary>
    /// Consumes pending samples and returns the next completed signature, or null if there
    /// isn't enough data (fewer than 128 unconsumed samples) to produce one.
    /// </summary>
    public DecodedMessage? GetNextSignature()
    {
        if (_inputPendingProcessing.Count - _samplesProcessed < 128)
        {
            return null;
        }

        while (_inputPendingProcessing.Count - _samplesProcessed >= 128
               && (_nextSignature.NumberSamples / (double)_nextSignature.SampleRateHz < MaxTimeSeconds
                   || _nextSignature.FrequencyBandToSoundPeaks.Values.Sum(v => v.Count) < MaxPeaks))
        {
            var chunk = _inputPendingProcessing.GetRange(_samplesProcessed, 128);
            ProcessInput(chunk);
            _samplesProcessed += 128;
        }

        var returned = _nextSignature;

        _nextSignature = NewSignature();
        Array.Clear(_ringBufferOfSamples);
        _ringBufferPosition = 0;
        _fftOutputs = NewFftHistory();
        _spreadFftsOutput = NewFftHistory();

        return returned;
    }

    private void ProcessInput(List<short> samples)
    {
        _nextSignature.NumberSamples += samples.Count;
        for (int offset = 0; offset < samples.Count; offset += 128)
        {
            int len = Math.Min(128, samples.Count - offset);
            DoFft(samples.GetRange(offset, len));
            DoPeakSpreadingAndRecognition();
        }
    }

    private void DoFft(List<short> batch)
    {
        var intBatch = new int[batch.Count];
        for (int i = 0; i < batch.Count; i++)
        {
            intBatch[i] = batch[i];
        }

        Array.Copy(intBatch, 0, _ringBufferOfSamples, _ringBufferPosition, intBatch.Length);
        _ringBufferPosition = (_ringBufferPosition + intBatch.Length) % RingBufferSamplesSize;

        var buffer = new Complex[RingBufferSamplesSize];
        for (int i = 0; i < RingBufferSamplesSize; i++)
        {
            int srcIndex = (_ringBufferPosition + i) % RingBufferSamplesSize;
            buffer[i] = new Complex(HanningMatrix[i] * _ringBufferOfSamples[srcIndex], 0);
        }

        Fourier.Forward(buffer, FourierOptions.Matlab);

        var fftResults = new double[FftBins];
        for (int k = 0; k < FftBins; k++)
        {
            double magSq = (buffer[k].Real * buffer[k].Real + buffer[k].Imaginary * buffer[k].Imaginary) / 131072.0; // 1 << 17
            fftResults[k] = Math.Max(magSq, 1e-10);
        }

        _fftOutputs.Append(fftResults);
    }

    private void DoPeakSpreadingAndRecognition()
    {
        DoPeakSpreading();
        if (_spreadFftsOutput.NumWritten >= 46)
        {
            DoPeakRecognition();
        }
    }

    private void DoPeakSpreading()
    {
        var originLastFft = _fftOutputs[_fftOutputs.Position - 1];
        var spreadLastFft = (double[])originLastFft.Clone();

        for (int position = 0; position < FftBins; position++)
        {
            if (position < 1023)
            {
                double m = spreadLastFft[position];
                int upper = Math.Min(position + 2, FftBins - 1);
                for (int j = position + 1; j <= upper; j++)
                {
                    if (spreadLastFft[j] > m)
                    {
                        m = spreadLastFft[j];
                    }
                }
                spreadLastFft[position] = m;
            }

            double maxValue = spreadLastFft[position];
            foreach (int formerFftNum in FormerFftOffsets)
            {
                var formerFftOutput = _spreadFftsOutput[_spreadFftsOutput.Position + formerFftNum];
                if (formerFftOutput[position] > maxValue)
                {
                    maxValue = formerFftOutput[position];
                }
                formerFftOutput[position] = maxValue;
            }
        }

        _spreadFftsOutput.Append(spreadLastFft);
    }

    private void DoPeakRecognition()
    {
        var fftMinus46 = _fftOutputs[_fftOutputs.Position - 46];
        var fftMinus49 = _spreadFftsOutput[_spreadFftsOutput.Position - 49];

        for (int binPosition = 10; binPosition < 1015; binPosition++)
        {
            if (!(fftMinus46[binPosition] >= 1.0 / 64 && fftMinus46[binPosition] >= fftMinus49[binPosition - 1]))
            {
                continue;
            }

            double maxNeighborInFftMinus49 = 0;
            foreach (int neighborOffset in NeighborOffsets)
            {
                maxNeighborInFftMinus49 = Math.Max(fftMinus49[binPosition + neighborOffset], maxNeighborInFftMinus49);
            }

            if (!(fftMinus46[binPosition] > maxNeighborInFftMinus49))
            {
                continue;
            }

            double maxNeighborInOtherAdjacentFfts = maxNeighborInFftMinus49;
            foreach (int otherOffset in OtherOffsets)
            {
                var other = _spreadFftsOutput[_spreadFftsOutput.Position + otherOffset];
                maxNeighborInOtherAdjacentFfts = Math.Max(other[binPosition - 1], maxNeighborInOtherAdjacentFfts);
            }

            if (!(fftMinus46[binPosition] > maxNeighborInOtherAdjacentFfts))
            {
                continue;
            }

            int fftNumber = _spreadFftsOutput.NumWritten - 46;

            double peakMagnitude = Math.Log(Math.Max(1.0 / 64, fftMinus46[binPosition])) * 1477.3 + 6144;
            double peakMagnitudeBefore = Math.Log(Math.Max(1.0 / 64, fftMinus46[binPosition - 1])) * 1477.3 + 6144;
            double peakMagnitudeAfter = Math.Log(Math.Max(1.0 / 64, fftMinus46[binPosition + 1])) * 1477.3 + 6144;

            double peakVariation1 = peakMagnitude * 2 - peakMagnitudeBefore - peakMagnitudeAfter;
            if (peakVariation1 <= 0)
            {
                // Numerically degenerate frame (extremely rare); skip rather than throw so a single
                // noisy frame can't tear down an otherwise-good recognition attempt.
                continue;
            }

            double peakVariation2 = (peakMagnitudeAfter - peakMagnitudeBefore) * 32 / peakVariation1;
            double correctedPeakFrequencyBin = binPosition * 64 + peakVariation2;

            double frequencyHz = correctedPeakFrequencyBin * (16000.0 / 2 / 1024 / 64);
            FrequencyBand band;
            if (frequencyHz < 250)
            {
                continue;
            }
            else if (frequencyHz < 520)
            {
                band = FrequencyBand.Band250To520;
            }
            else if (frequencyHz < 1450)
            {
                band = FrequencyBand.Band520To1450;
            }
            else if (frequencyHz < 3500)
            {
                band = FrequencyBand.Band1450To3500;
            }
            else if (frequencyHz <= 5500)
            {
                band = FrequencyBand.Band3500To5500;
            }
            else
            {
                continue;
            }

            if (!_nextSignature.FrequencyBandToSoundPeaks.TryGetValue(band, out var list))
            {
                list = new List<FrequencyPeak>();
                _nextSignature.FrequencyBandToSoundPeaks[band] = list;
            }

            list.Add(new FrequencyPeak(fftNumber, (int)peakMagnitude, (int)correctedPeakFrequencyBin, 16000));
        }
    }
}
