using NAudio.Wave;
using System;

namespace MeaSound
{
    /// <summary>
    /// Helper methods for writing PCM samples to <see cref="WaveFileWriter"/>.
    /// </summary>
    internal static class PcmWriterHelper
    {
        public static void WritePcmSamples(WaveFileWriter writer, float[] samples, int bitDepth)
        {
            switch (bitDepth)
            {
                case 16:
                    WritePcm16(writer, samples);
                    break;
                case 24:
                    WritePcm24(writer, samples);
                    break;
                default:
                    WritePcm32(writer, samples);
                    break;
            }
        }

        private static void WritePcm16(WaveFileWriter writer, float[] samples)
        {
            byte[] pcm = new byte[samples.Length * 2];
            int offset = 0;

            for (int i = 0; i < samples.Length; i++)
            {
                short value = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
                pcm[offset++] = (byte)(value & 0xFF);
                pcm[offset++] = (byte)((value >> 8) & 0xFF);
            }

            writer.Write(pcm, 0, pcm.Length);
        }

        private static void WritePcm24(WaveFileWriter writer, float[] samples)
        {
            byte[] pcm = new byte[samples.Length * 3];
            int offset = 0;

            for (int i = 0; i < samples.Length; i++)
            {
                int value = (int)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 8388607f);
                pcm[offset++] = (byte)(value & 0xFF);
                pcm[offset++] = (byte)((value >> 8) & 0xFF);
                pcm[offset++] = (byte)((value >> 16) & 0xFF);
            }

            writer.Write(pcm, 0, pcm.Length);
        }

        private static void WritePcm32(WaveFileWriter writer, float[] samples)
        {
            byte[] pcm = new byte[samples.Length * 4];
            int offset = 0;

            for (int i = 0; i < samples.Length; i++)
            {
                int value = (int)Math.Round(Math.Clamp(samples[i], -1f, 1f) * int.MaxValue);
                pcm[offset++] = (byte)(value & 0xFF);
                pcm[offset++] = (byte)((value >> 8) & 0xFF);
                pcm[offset++] = (byte)((value >> 16) & 0xFF);
                pcm[offset++] = (byte)((value >> 24) & 0xFF);
            }

            writer.Write(pcm, 0, pcm.Length);
        }
    }
}
