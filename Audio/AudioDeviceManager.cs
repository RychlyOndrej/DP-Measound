using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;

namespace MeaSound
{
    internal class AudioDeviceManager
    {
        private readonly MMDeviceEnumerator enumerator = new();

        private readonly Dictionary<string, MMDevice> _inputMap = new();
        private readonly Dictionary<string, MMDevice> _outputMap = new();

        public event Action<string>? OnError;

        public List<AudioDeviceInfo> GetInputDevices()
        {
            try
            {
                _inputMap.Clear();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

                return devices.Select(d =>
                {
                    _inputMap[d.ID] = d;
                    return new AudioDeviceInfo { Id = d.ID, Name = d.FriendlyName };
                }).ToList();
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Chyba pøi naèítání vstupních zaøízení: {ex.Message}");
                return new();
            }
        }

        public MMDevice? ResolveInput(AudioDeviceInfo? info)
        {
            if (info == null) return null;
            return _inputMap.TryGetValue(info.Id, out var dev) ? dev : null;
        }

        public MMDevice? CreateInputDeviceById(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try { return new MMDeviceEnumerator().GetDevice(id); }
            catch (Exception ex) { OnError?.Invoke($"Chyba pøi otevøení vstupního zaøízení: {ex.Message}"); return null; }
        }

        public List<AudioDeviceInfo> GetOutputDevices()
        {
            try
            {
                _outputMap.Clear();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                return devices.Select(d =>
                {
                    _outputMap[d.ID] = d;
                    return new AudioDeviceInfo { Id = d.ID, Name = d.FriendlyName };
                }).ToList();
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Chyba pøi naèítání výstupních zaøízení: {ex.Message}");
                return new();
            }
        }

        public MMDevice? ResolveOutput(AudioDeviceInfo? info)
        {
            if (info == null) return null;
            return _outputMap.TryGetValue(info.Id, out var dev) ? dev : null;
        }

        public MMDevice? CreateOutputDeviceById(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try { return new MMDeviceEnumerator().GetDevice(id); }
            catch (Exception ex) { OnError?.Invoke($"Chyba pøi otevøení výstupního zaøízení: {ex.Message}"); return null; }
        }
    }
}
