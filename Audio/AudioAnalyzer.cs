using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PartyTime.Audio;

internal sealed class AudioAnalyzer
{
    public const int SampleRate = 48_000;
    public const int BandCount = 32;
    private const int FftSize = 2048;
    private const int Hop = 512;

    private static readonly double[] Hann = new double[FftSize];
    private static readonly int[] BitReversed = new int[FftSize];
    private static readonly double[] RootReal = new double[FftSize];
    private static readonly double[] RootImaginary = new double[FftSize];
    private static readonly int[] BandEdges = new int[BandCount + 1];

    private readonly Complex[] _samples = new Complex[FftSize];
    private readonly double[] _spectrumReal = new double[FftSize];
    private readonly double[] _spectrumImaginary = new double[FftSize];
    private readonly double[] _magnitudes = new double[BandEdges[^1]];
    private readonly (double Magnitude, double Weighted)[] _fullBandPeaks = new (double, double)[BandCount];
    private int _position;
    private int _samplesUntilAnalysis = FftSize;
    private long _lastPacket;
    private FrequencyRange _range = FrequencyRange.Full;
    private SignalHistory _focused = new();
    private SignalHistory _full = new();
    private SignalHistory _low = new();
    private SignalHistory _mid = new();
    private SignalHistory _high = new();

    private sealed class SignalHistory
    {
        public readonly double[] PreviousBands = new double[BandCount];
        public long LastBeat;
        public float BeatStrength;
        public double Peak;
        public double FluxAverage;
        public double DriveAverage;
    }

    static AudioAnalyzer()
    {
        int bits = BitOperations.Log2(FftSize);
        for (int i = 0; i < FftSize; i++)
        {
            Hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
            int reversed = 0;
            for (int bit = 0; bit < bits; bit++)
                reversed = (reversed << 1) | ((i >> bit) & 1);
            BitReversed[i] = reversed;
        }

        // Store the roots for each butterfly stage contiguously so SIMD can load adjacent lanes.
        for (int half = 1; half < FftSize; half *= 2)
        {
            int step = FftSize / (half * 2);
            for (int j = 0; j < half; j++)
            {
                var root = Complex.FromPolarCoordinates(1, -2 * Math.PI * (j * step) / FftSize);
                RootReal[half + j] = root.Real;
                RootImaginary[half + j] = root.Imaginary;
            }
        }

        for (int i = 0; i <= BandCount; i++)
        {
            double hertz = 38 * Math.Pow(13_000.0 / 38, i / (double)BandCount);
            BandEdges[i] = Math.Max(1, (int)Math.Round(hertz * FftSize / SampleRate));
        }
    }

    public static bool IncludesBand(int band, FrequencyRange range)
        => Math.Max(BandEdges[band] + 1, BandEdges[band + 1]) > FirstBin(range)
            && BandEdges[band] < EndBin(range);

    private static int FirstBin(FrequencyRange range)
        => range == FrequencyRange.Full ? 1 : (range.Low * FftSize + SampleRate - 1) / SampleRate;

    private static int EndBin(FrequencyRange range)
        => Math.Min(BandEdges[^1], range.High * FftSize / SampleRate + 1);

    public AudioFrame? Push(ReadOnlySpan<byte> buffer, long now, FrequencyRange range)
    {
        bool changed = _range != range;
        bool interrupted = _lastPacket != 0 && Stopwatch.GetElapsedTime(_lastPacket, now).TotalSeconds > 0.22;
        if (changed || interrupted)
        {
            _range = range;
            _focused = new();
            if (interrupted)
            {
                _full = new();
                _low = new();
                _mid = new();
                _high = new();
            }
            if (interrupted)
                _samplesUntilAnalysis = FftSize;
        }
        _lastPacket = now;

        AudioFrame? frame = null;
        var samples = MemoryMarshal.Cast<byte, float>(buffer);
        for (int i = 0; i + 1 < samples.Length; i += 2)
        {
            float left = samples[i];
            float right = samples[i + 1];
            _samples[_position] = new Complex(float.IsFinite(left) ? left : 0, float.IsFinite(right) ? right : 0);
            _position = (_position + 1) & (FftSize - 1);
            if (--_samplesUntilAnalysis == 0)
            {
                // Intermediate hops update the onset detector. Only the final hop leaves this packet.
                bool publish = (samples.Length - i) / 2 <= Hop;
                frame = Analyze(now, publish);
                _samplesUntilAnalysis = Hop;
            }
        }
        return frame;
    }

    private AudioFrame? Analyze(long now, bool publish)
    {
        double energy = 0;
        for (int i = 0; i < FftSize; i++)
        {
            Complex sample = _samples[(_position + i) & (FftSize - 1)];
            energy += (sample.Real * sample.Real + sample.Imaginary * sample.Imaginary) * 0.5;
            int index = BitReversed[i];
            _spectrumReal[index] = sample.Real * Hann[i];
            _spectrumImaginary[index] = sample.Imaginary * Hann[i];
        }

        if (energy == 0)
            Array.Clear(_magnitudes);
        else
        {
            TransformSpectrum();
            for (int i = 1; i < _magnitudes.Length; i++)
            {
                // The two mirrored bins contain the combined power of the packed left and right channels.
                double real = _spectrumReal[i], imaginary = _spectrumImaginary[i];
                int mirror = FftSize - i;
                double mirroredReal = _spectrumReal[mirror], mirroredImaginary = _spectrumImaginary[mirror];
                double power = real * real + imaginary * imaginary + mirroredReal * mirroredReal + mirroredImaginary * mirroredImaginary;
                _magnitudes[i] = Math.Sqrt(power) * (2.0 / FftSize);
            }
        }

        var full = AnalyzeSignal(now, FrequencyRange.Full, _full, energy, publish);
        var focused = _range == FrequencyRange.Full ? full : AnalyzeSignal(now, _range, _focused, energy, publish);
        var low = AnalyzeSignal(now, FrequencyRange.Bass, _low, energy, publish);
        var mid = AnalyzeSignal(now, FrequencyRange.Mids, _mid, energy, publish);
        var high = AnalyzeSignal(now, FrequencyRange.Treble, _high, energy, publish);
        return publish ? new AudioFrame(now, focused!, full!, low!, mid!, high!) : null;
    }

    private void TransformSpectrum()
    {
        for (int size = 2; size <= FftSize; size *= 2)
        {
            int half = size / 2;
            for (int start = 0; start < FftSize; start += size)
            {
                int j = 0;
                if (Vector.IsHardwareAccelerated)
                {
                    for (; j <= half - Vector<double>.Count; j += Vector<double>.Count)
                    {
                        int upper = start + j, lower = upper + half, root = half + j;
                        var real = new Vector<double>(_spectrumReal, upper);
                        var imaginary = new Vector<double>(_spectrumImaginary, upper);
                        var otherReal = new Vector<double>(_spectrumReal, lower);
                        var otherImaginary = new Vector<double>(_spectrumImaginary, lower);
                        var rootReal = new Vector<double>(RootReal, root);
                        var rootImaginary = new Vector<double>(RootImaginary, root);
                        var rotatedReal = otherReal * rootReal - otherImaginary * rootImaginary;
                        var rotatedImaginary = otherImaginary * rootReal + otherReal * rootImaginary;
                        (real + rotatedReal).CopyTo(_spectrumReal, upper);
                        (imaginary + rotatedImaginary).CopyTo(_spectrumImaginary, upper);
                        (real - rotatedReal).CopyTo(_spectrumReal, lower);
                        (imaginary - rotatedImaginary).CopyTo(_spectrumImaginary, lower);
                    }
                }
                for (; j < half; j++)
                {
                    int upper = start + j, lower = upper + half, root = half + j;
                    double real = _spectrumReal[upper], imaginary = _spectrumImaginary[upper];
                    double otherReal = _spectrumReal[lower], otherImaginary = _spectrumImaginary[lower];
                    double rotatedReal = otherReal * RootReal[root] - otherImaginary * RootImaginary[root];
                    double rotatedImaginary = otherImaginary * RootReal[root] + otherReal * RootImaginary[root];
                    _spectrumReal[upper] = real + rotatedReal;
                    _spectrumImaginary[upper] = imaginary + rotatedImaginary;
                    _spectrumReal[lower] = real - rotatedReal;
                    _spectrumImaginary[lower] = imaginary - rotatedImaginary;
                }
            }
        }
    }

    private AudioSignal? AnalyzeSignal(long now, FrequencyRange range, SignalHistory history, double energy, bool publish)
    {
        int firstBin = FirstBin(range);
        int endBin = EndBin(range);
        bool full = range == FrequencyRange.Full;
        double drive = 0;
        double focusedEnergy = 0;
        if (full)
        {
            // Full-mix level comes from the time-domain energy. Its drive only follows the kick bins.
            for (int i = 2; i <= 6; i++)
                drive = Math.Max(drive, _magnitudes[i]);
        }
        else
        {
            for (int i = firstBin; i < endBin; i++)
            {
                double magnitude = _magnitudes[i];
                focusedEnergy += magnitude * magnitude;
                drive = Math.Max(drive, magnitude);
            }
        }

        // A Hann window spreads a sinusoid's power over nearby bins. This scale matches time-domain RMS.
        double rms = full ? Math.Sqrt(energy / FftSize) : Math.Sqrt(focusedEnergy / 3);
        history.Peak = Math.Max(rms, history.Peak * 0.997);
        double gain = Math.Clamp(0.21 / Math.Max(0.04, history.Peak), 0.6, 5);

        double flux = 0;
        int activeBands = 0;
        var bands = publish ? new float[BandCount] : null;
        for (int i = 0; i < BandCount; i++)
        {
            int bandStart = BandEdges[i];
            int bandEnd = Math.Max(bandStart + 1, BandEdges[i + 1]);
            int start = Math.Max(firstBin, bandStart);
            int end = Math.Min(endBin, bandEnd);
            if (start >= end)
                continue;
            activeBands++;
            var (magnitude, weighted) = !full && start == bandStart && end == bandEnd
                ? _fullBandPeaks[i] : MeasureBand(start, end);
            if (full)
                _fullBandPeaks[i] = (magnitude, weighted);
            flux += Math.Max(0, weighted - history.PreviousBands[i]) * (full && i < 8 ? 1.35 : 1);
            history.PreviousBands[i] = weighted;
            if (bands != null && rms >= 0.0004)
                bands[i] = (float)Math.Clamp(Math.Log(1 + magnitude * gain * 36) / 2.6, 0, 1);
        }

        flux /= Math.Max(activeBands, 1);
        double driveRise = Math.Max(0, drive - history.DriveAverage) * gain;
        double onset = flux + 0.6 * driveRise;
        if (rms > 0.004 && onset > Math.Max(0.065, history.FluxAverage * 1.75 + 0.025)
            && (history.LastBeat == 0 || Stopwatch.GetElapsedTime(history.LastBeat, now).TotalSeconds >= 0.17))
        {
            history.LastBeat = now;
            history.BeatStrength = (float)Math.Clamp(0.45 + onset * 1.15, 0, 1);
        }

        history.FluxAverage += (onset - history.FluxAverage) * 0.045;
        history.DriveAverage += (drive - history.DriveAverage) * 0.075;
        return bands == null ? null : new AudioSignal(
            rms < 0.0004 ? 0 : (float)Math.Clamp(Math.Sqrt(rms * gain * 2.8), 0, 1),
            rms < 0.0004 ? 0 : (float)Math.Clamp(Math.Sqrt(drive * gain * 1.7), 0, 1),
            bands, history.LastBeat, history.BeatStrength, range);
    }

    private (double Magnitude, double Weighted) MeasureBand(int start, int end)
    {
        double magnitude = 0;
        for (int i = start; i < end; i++)
            magnitude = Math.Max(magnitude, _magnitudes[i]);
        return (magnitude, Math.Log(1 + 30 * magnitude));
    }
}
