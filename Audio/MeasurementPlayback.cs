using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeaSound
{
    /// <summary>
    /// WASAPI playback helper used during measurement to play test signals blocking.
    /// </summary>
    internal sealed class MeasurementPlayback : IDisposable
    {
        private WasapiOut? _out;
        private AudioFileReader? _reader;

        public void PlayFileBlocking(MMDevice outputDevice, string filePath, bool exclusive, CancellationToken token)
        {
            Stop();

            _reader = new AudioFileReader(filePath);

            int deviceSampleRate = outputDevice.AudioClient?.MixFormat?.SampleRate ?? _reader.WaveFormat.SampleRate;
            int deviceChannels = outputDevice.AudioClient?.MixFormat?.Channels ?? 2;

            ISampleProvider sp = _reader.ToSampleProvider();

            if (sp.WaveFormat.SampleRate != deviceSampleRate)
                sp = new WdlResamplingSampleProvider(sp, deviceSampleRate);

            if (sp.WaveFormat.Channels == 1)
                sp = sp.ToStereo();

            if (sp.WaveFormat.Channels != deviceChannels)
            {
                sp = deviceChannels switch
                {
                    1 => sp.ToMono(),
                    2 => sp.ToStereo(),
                    _ => sp
                };
            }

            // Apply calibration gain
            float calGain = Preferences.Load().GetCalibrationGainLinear();
            sp = new VolumeSampleProvider(sp) { Volume = calGain };

            var waveProvider = new SampleToWaveProvider(sp);

            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

            _out = new WasapiOut(outputDevice, exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, false, 50);
            _out.Init(waveProvider);
            _out.PlaybackStopped += (_, __) => tcs.TrySetResult(true);
            _out.Play();

            using (token.Register(() =>
            {
                try { _out?.Stop(); } catch { }
                tcs.TrySetCanceled(token);
            }))
            {
                tcs.Task.GetAwaiter().GetResult();
            }
        }

        public void Stop()
        {
            try { _out?.Stop(); } catch { }
            try { _out?.Dispose(); } catch { }
            _out = null;

            try { _reader?.Dispose(); } catch { }
            _reader = null;
        }

        public void Dispose() => Stop();
    }
}
