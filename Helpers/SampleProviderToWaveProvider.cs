using NAudio.Wave;
using System;

namespace MeaSound
{
    /// <summary>
    /// Simple converter from ISampleProvider to IWaveProvider.
    /// </summary>
    internal class SampleProviderToWaveProvider : IWaveProvider
    {
        private readonly ISampleProvider _source;
        private readonly float[] _buffer;

        public WaveFormat WaveFormat { get; }

        public SampleProviderToWaveProvider(ISampleProvider source, int bufferSize = 8192)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, source.WaveFormat.Channels);
            _buffer = new float[bufferSize];
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int samplesRequested = count / sizeof(float);
            if (samplesRequested <= 0)
                return 0;

            int samplesRead = _source.Read(_buffer, 0, Math.Min(samplesRequested, _buffer.Length));
            if (samplesRead <= 0)
                return 0;

            Buffer.BlockCopy(_buffer, 0, buffer, offset, samplesRead * sizeof(float));
            return samplesRead * sizeof(float);
        }
    }
}
