using NAudio.Wave;
using System;

namespace MeaSound
{
    /// <summary>
    /// Expands a mono ISampleProvider to N channels by duplicating each sample.
    /// Used to send the test signal to every physical ASIO output jack.
    /// </summary>
    internal class MonoToMultiChannelProvider : ISampleProvider
    {
        private readonly ISampleProvider _mono;
        private readonly int _channels;
        private float[] _monoBuffer = Array.Empty<float>();

        public WaveFormat WaveFormat { get; }

        public MonoToMultiChannelProvider(ISampleProvider mono, int channels)
        {
            ArgumentNullException.ThrowIfNull(mono);
            if (mono.WaveFormat.Channels != 1)
                throw new ArgumentException("Source must be mono.", nameof(mono));

            _mono = mono;
            _channels = channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(mono.WaveFormat.SampleRate, channels);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int framesRequested = count / _channels;
            if (_monoBuffer.Length < framesRequested)
                _monoBuffer = new float[framesRequested];

            int framesRead = _mono.Read(_monoBuffer, 0, framesRequested);
            if (framesRead <= 0) return 0;

            int outIdx = offset;
            for (int i = 0; i < framesRead; i++)
            {
                float sample = _monoBuffer[i];
                for (int ch = 0; ch < _channels; ch++)
                    buffer[outIdx++] = sample;
            }

            return framesRead * _channels;
        }
    }
}
