using NAudio.Wave;

namespace MeaSound
{
    internal sealed class SampleToPcmWaveProvider : IWaveProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _bitDepth;
        private float[] _sampleBuffer = [];

        public SampleToPcmWaveProvider(ISampleProvider source, int bitDepth)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (bitDepth is not (16 or 24 or 32))
                throw new ArgumentOutOfRangeException(nameof(bitDepth), "Only 16, 24 or 32-bit PCM is supported.");

            _source = source;
            _bitDepth = bitDepth;
            WaveFormat = new WaveFormat(source.WaveFormat.SampleRate, bitDepth, source.WaveFormat.Channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            int bytesPerSample = _bitDepth / 8;
            int requestedSamples = count / bytesPerSample;
            if (requestedSamples <= 0)
                return 0;

            if (_sampleBuffer.Length < requestedSamples)
                _sampleBuffer = new float[requestedSamples];

            int samplesRead = _source.Read(_sampleBuffer, 0, requestedSamples);
            int writeOffset = offset;

            for (int i = 0; i < samplesRead; i++)
            {
                float clamped = Math.Clamp(_sampleBuffer[i], -1f, 1f);
                switch (_bitDepth)
                {
                    case 16:
                        short pcm16 = (short)Math.Round(clamped * short.MaxValue);
                        buffer[writeOffset++] = (byte)(pcm16 & 0xFF);
                        buffer[writeOffset++] = (byte)((pcm16 >> 8) & 0xFF);
                        break;
                    case 24:
                        int pcm24 = (int)Math.Round(clamped * 8388607f);
                        buffer[writeOffset++] = (byte)(pcm24 & 0xFF);
                        buffer[writeOffset++] = (byte)((pcm24 >> 8) & 0xFF);
                        buffer[writeOffset++] = (byte)((pcm24 >> 16) & 0xFF);
                        break;
                    default:
                        int pcm32 = (int)Math.Round(clamped * int.MaxValue);
                        buffer[writeOffset++] = (byte)(pcm32 & 0xFF);
                        buffer[writeOffset++] = (byte)((pcm32 >> 8) & 0xFF);
                        buffer[writeOffset++] = (byte)((pcm32 >> 16) & 0xFF);
                        buffer[writeOffset++] = (byte)((pcm32 >> 24) & 0xFF);
                        break;
                }
            }

            return samplesRead * bytesPerSample;
        }
    }
}
