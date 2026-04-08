using System;
using System.IO;
using System.Text.Json;

namespace MeaSound
{
    public class Preferences
    {
        private static readonly string AppFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeaSound");
        private static readonly string PrefFile = Path.Combine(AppFolder, "preferences.json");

        public bool UseDarkTheme { get; set; } = false;
        public bool SaveChartsWithWhiteBackground { get; set; } = true;
        public bool EnableLanProgress { get; set; } = false;
        public string LanProgressPassword { get; set; } = string.Empty;
        public bool LanProgressShowStatus { get; set; } = true;
        public bool LanProgressShowMeasurementIndex { get; set; } = true;
        public bool LanProgressShowStepProgress { get; set; } = true;
        public bool LanProgressShowTimestamp { get; set; } = true;
        public int LanProgressRefreshMs { get; set; } = 1000;

        /// <summary>Output calibration level in dB (set via MicCalibrationWindow). Applied to all playback.</summary>
        public double CalibrationGainDb { get; set; } = -12.0;

        /// <summary>Returns the calibration gain as a linear multiplier.</summary>
        public float GetCalibrationGainLinear() =>
            (float)Math.Pow(10.0, CalibrationGainDb / 20.0);

        public static Preferences Load()
        {
            try
            {
                if (!Directory.Exists(AppFolder))
                    Directory.CreateDirectory(AppFolder);

                if (!File.Exists(PrefFile))
                    return new Preferences();

                var json = File.ReadAllText(PrefFile);
                return JsonSerializer.Deserialize<Preferences>(json) ?? new Preferences();
            }
            catch
            {
                return new Preferences();
            }
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(AppFolder))
                    Directory.CreateDirectory(AppFolder);

                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(PrefFile, json);
            }
            catch
            {
                // ignore
            }
        }
    }
}
