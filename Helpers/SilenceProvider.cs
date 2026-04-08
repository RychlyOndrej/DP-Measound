using NAudio.Wave;
using System;

namespace MeaSound
{
    /// <summary>
    /// Simple silence provider for ASIO full-duplex mode when no playback is needed.
    /// </summary>
    internal class SilenceProvider : ISampleProvider
    {
        public WaveFormat WaveFormat { get; }

        public SilenceProvider(WaveFormat waveFormat)
        {
            WaveFormat = waveFormat ?? throw new ArgumentNullException(nameof(waveFormat));
        }

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
    }
}
