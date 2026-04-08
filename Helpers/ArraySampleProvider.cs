using NAudio.Wave;
using System;

namespace MeaSound
{
    /// <summary>
    /// Simple ISampleProvider wrapper for a float array.
    /// </summary>
    internal class ArraySampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public ArraySampleProvider(float[] samples, int sampleRate, int channels)
        {
            ArgumentNullException.ThrowIfNull(samples);
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _position = 0;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesToRead = Math.Min(count, _samples.Length - _position);
            if (samplesToRead <= 0)
                return 0;

            Array.Copy(_samples, _position, buffer, offset, samplesToRead);
            _position += samplesToRead;

            return samplesToRead;
        }
    }
}
