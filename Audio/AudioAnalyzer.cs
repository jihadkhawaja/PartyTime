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
    private static readonly Complex[] Roots = new Complex[FftSize / 2];
    private static readonly int[] BandEdges = new int[BandCount + 1];

    private readonly Complex[] _samples = new Complex[FftSize];
    private readonly Complex[] _spectrum = new Complex[FftSize];
    private readonly double[] _magnitudes = new double[BandEdges[^1]];
    private readonly double[] _previousBands = new double[BandCount];
    private int _position;
    private int _samplesUntilAnalysis = FftSize;
    private long _lastPacket;
    private long _lastBeat;
    private float _beatStrength;
    private double _peak;
    private double _fluxAverage;
    private double _driveAverage;
    private FrequencyRange _range = FrequencyRange.Full;

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

        for (int i = 0; i < Roots.Length; i++)
            Roots[i] = Complex.FromPolarCoordinates(1, -2 * Math.PI * i / FftSize);

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
            _lastBeat = 0;
            _beatStrength = 0;
            _fluxAverage = 0;
            _driveAverage = 0;
            Array.Clear(_previousBands);
            if (changed)
                _peak = 0;
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
                frame = Analyze(now);
                _samplesUntilAnalysis = Hop;
            }
        }
        return frame;
    }

    private AudioFrame Analyze(long now)
    {
        double energy = 0;
        for (int i = 0; i < FftSize; i++)
        {
            Complex sample = _samples[(_position + i) & (FftSize - 1)];
            energy += (sample.Real * sample.Real + sample.Imaginary * sample.Imaginary) * 0.5;
            _spectrum[BitReversed[i]] = sample * Hann[i];
        }

        for (int size = 2; size <= FftSize; size *= 2)
        {
            int half = size / 2;
            int rootStep = FftSize / size;
            for (int start = 0; start < FftSize; start += size)
            {
                for (int j = 0; j < half; j++)
                {
                    var even = _spectrum[start + j];
                    var odd = _spectrum[start + j + half] * Roots[j * rootStep];
                    _spectrum[start + j] = even + odd;
                    _spectrum[start + j + half] = even - odd;
                }
            }
        }

        for (int i = 1; i < _magnitudes.Length; i++)
        {
            // The two mirrored bins contain the combined power of the packed left and right channels.
            Complex positive = _spectrum[i];
            Complex negative = _spectrum[FftSize - i];
            double power = positive.Real * positive.Real + positive.Imaginary * positive.Imaginary
                + negative.Real * negative.Real + negative.Imaginary * negative.Imaginary;
            _magnitudes[i] = Math.Sqrt(power) * (2.0 / FftSize);
        }

        int firstBin = FirstBin(_range);
        int endBin = EndBin(_range);
        bool full = _range == FrequencyRange.Full;
        double drive = 0;
        double focusedEnergy = 0;
        for (int i = firstBin; i < endBin; i++)
        {
            double magnitude = _magnitudes[i];
            focusedEnergy += magnitude * magnitude;
            if (!full || i is >= 2 and <= 6)
                drive = Math.Max(drive, magnitude);
        }

        // A Hann window spreads a sinusoid's power over nearby bins. This scale matches time-domain RMS.
        double rms = full ? Math.Sqrt(energy / FftSize) : Math.Sqrt(focusedEnergy / 3);
        _peak = Math.Max(rms, _peak * 0.997);
        double gain = Math.Clamp(0.21 / Math.Max(0.04, _peak), 0.6, 5);

        double flux = 0;
        int activeBands = 0;
        var bands = new float[BandCount];
        for (int i = 0; i < BandCount; i++)
        {
            int start = Math.Max(firstBin, BandEdges[i]);
            int end = Math.Min(endBin, Math.Max(BandEdges[i] + 1, BandEdges[i + 1]));
            if (start >= end)
                continue;
            activeBands++;
            double magnitude = 0;
            for (int j = start; j < end; j++)
                magnitude = Math.Max(magnitude, _magnitudes[j]);

            double weighted = Math.Log(1 + 30 * magnitude);
            flux += Math.Max(0, weighted - _previousBands[i]) * (full && i < 8 ? 1.35 : 1);
            _previousBands[i] = weighted;
            if (rms >= 0.0004)
                bands[i] = (float)Math.Clamp(Math.Log(1 + magnitude * gain * 36) / 2.6, 0, 1);
        }

        flux /= Math.Max(activeBands, 1);
        double driveRise = Math.Max(0, drive - _driveAverage) * gain;
        double onset = flux + 0.6 * driveRise;
        if (rms > 0.004 && onset > Math.Max(0.065, _fluxAverage * 1.75 + 0.025)
            && (_lastBeat == 0 || Stopwatch.GetElapsedTime(_lastBeat, now).TotalSeconds >= 0.17))
        {
            _lastBeat = now;
            _beatStrength = (float)Math.Clamp(0.45 + onset * 1.15, 0, 1);
        }

        _fluxAverage += (onset - _fluxAverage) * 0.045;
        _driveAverage += (drive - _driveAverage) * 0.075;
        return new AudioFrame(
            rms < 0.0004 ? 0 : (float)Math.Clamp(Math.Sqrt(rms * gain * 2.8), 0, 1),
            rms < 0.0004 ? 0 : (float)Math.Clamp(Math.Sqrt(drive * gain * 1.7), 0, 1),
            bands, now, _lastBeat, _beatStrength, _range);
    }
}
