using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ScottPlot;
using ScottPlot.Plottables;
using ScottPlot.WPF;
using System.Threading;

namespace MeaSound
{
	internal class MeasurementManager
	{
		#region Fields & Dependencies

		private readonly AudioRecorder _recorder;
		private SerialPortManager _serialManager;
		private ExcelDataSaver _excelSaver;
		private readonly WpfPlot _wpfPlot;
		private readonly WpfPlot _spectrogramPlot;
		private SignalAnalyzer _analyzer;
		private readonly AudioDeviceManager _audioDeviceManager;
		private readonly WpfPlot _fftPlot;

		#endregion

		#region Session State

		private string? _currentSessionFolder;
		private int _nextMeasurementNumber = 1;

		private double _globalMaxMagnitudeDb = double.MinValue;
		private Dictionary<int, SortedDictionary<int, double>> _rawPolarData = new();

		private SyncBufferLayout? _lastSyncLayout;
		public SyncBufferLayout? LastSyncLayout => _lastSyncLayout;

		public Action<int> OnMeasurementProgress { get; set; }
		public Action<int, int>? OnStepProgress { get; set; }
		public Action<string>? OnStatusChanged { get; set; }

		/// <summary>Wiener deconvolution regularization parameter set before measurement starts.</summary>
		public double WienerLambda { get; set; } = 1e-5;

		private void ReportStatus(string message)
		{
			if (string.IsNullOrWhiteSpace(message))
				return;

			OnStatusChanged?.Invoke(message);
		}

		private void ReportStepProgress(int completedSteps, int totalSteps)
		{
			OnStepProgress?.Invoke(completedSteps, totalSteps);
		}

		private string _sessionMicName = string.Empty;
		private string _sessionSpeakerName = string.Empty;
		private string _sessionDistance = string.Empty;
		private TestSignalType _sessionSignalType;
		private int _sessionSampleRate;
		private int _sessionBitDepth;
		private int _sessionStartFreq;
		private int _sessionEndFreq;
		private float _sessionConstantFreq;
		private float[]? _sessionPlayMultipleFreqs;
		private string _sessionDriverType = string.Empty;

		#endregion

		#region Constructor

		public MeasurementManager(
			AudioRecorder recorder,
			SerialPortManager serialManager,
			ExcelDataSaver excelSaver,
			WpfPlot wpfPlot,
			WpfPlot spectrogramPlot,
			AudioDeviceManager audioDeviceManager,
			WpfPlot fftPlot = null)
		{
			_recorder = recorder;
			_serialManager = serialManager;
			_excelSaver = excelSaver;
			_wpfPlot = wpfPlot;
			_spectrogramPlot = spectrogramPlot;
			_audioDeviceManager = audioDeviceManager;
			_fftPlot = fftPlot;

			bool isDarkMode = ThemeManager.Instance.IsDarkMode;
			if (_spectrogramPlot != null) ScottPlotThemeHelper.ApplyTheme(_spectrogramPlot, isDarkMode);
			if (_fftPlot != null) ScottPlotThemeHelper.ApplyTheme(_fftPlot, isDarkMode);
		}

		#endregion

		#region Session Management

		public void ResetSession()
		{
			_currentSessionFolder = null;
			_nextMeasurementNumber = 1;
			_globalMaxMagnitudeDb = double.MinValue;
			_rawPolarData.Clear();
			Debug.WriteLine("[MeasurementManager] Session reset");
		}

		private static string BuildSessionFolderName(string sessionTimestamp, string? sessionSuffix)
		{
			string sanitizedSuffix = SanitizeSessionSuffix(sessionSuffix);
			return string.IsNullOrWhiteSpace(sanitizedSuffix)
				? $"MeaSound_{sessionTimestamp}"
				: $"MeaSound_{sessionTimestamp}_{sanitizedSuffix}";
		}

		private static string SanitizeSessionSuffix(string? sessionSuffix)
		{
			if (string.IsNullOrWhiteSpace(sessionSuffix))
				return string.Empty;

			string trimmed = sessionSuffix.Trim();
			char[] invalidChars = Path.GetInvalidFileNameChars();
			var cleaned = new string(trimmed
				.Select(ch => invalidChars.Contains(ch) ? '_' : ch)
				.ToArray());

			while (cleaned.Contains("  "))
				cleaned = cleaned.Replace("  ", " ");

			return cleaned.Trim(' ', '.', '_');
		}

		private string EnsureSessionFolder(string baseSavePath, string? sessionSuffix = null)
		{
			if (string.IsNullOrWhiteSpace(_currentSessionFolder) || !Directory.Exists(_currentSessionFolder))
			{
				string sessionTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
				string sessionFolderName = BuildSessionFolderName(sessionTimestamp, sessionSuffix);
				_currentSessionFolder = Path.Combine(baseSavePath, sessionFolderName);
				Directory.CreateDirectory(_currentSessionFolder);
				_nextMeasurementNumber = 1;
				Debug.WriteLine($"Vytvořena nová session složka: {_currentSessionFolder}");
			}
			return _currentSessionFolder;
		}

		#endregion

		#region Recording Helpers

		private System.Threading.Tasks.Task<string> RecordSegmentAsync(
			string micFilePath, int durationSeconds, int sampleRate, int bitDepth, bool isFloat,
			CancellationToken token, string? refFilePath = null)
		{
			var tcs = new System.Threading.Tasks.TaskCompletionSource<string>(
				System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

			void Handler(string p)
			{
				try { _recorder.OnRecordingStopped -= Handler; } catch { }
				tcs.TrySetResult(p);
			}

			_recorder.OnRecordingStopped += Handler;

			try { _recorder.StartRecording(micFilePath, sampleRate, bitDepth, isFloat, refFilePath: refFilePath); }
			catch (Exception ex)
			{
				try { _recorder.OnRecordingStopped -= Handler; } catch { }
				tcs.TrySetException(ex);
				return tcs.Task;
			}

			_ = System.Threading.Tasks.Task.Run(async () =>
			{
				try { for (int i = 0; i < durationSeconds * 10; i++) { if (token.IsCancellationRequested) break; await System.Threading.Tasks.Task.Delay(100, token); } } catch { }
				try { _recorder.StopRecording(); } catch { }
			});

			return tcs.Task;
		}

		public void RecordNoiseFloor(string measurementFolder, int durationSeconds, int sampleRate,
			int bitDepth, bool isFloat, CancellationToken token)
		{
			if (token.IsCancellationRequested) return;
			string noiseFile = Path.Combine(measurementFolder, "NoiseFloor.wav");
			try { _ = RecordSegmentAsync(noiseFile, durationSeconds, sampleRate, bitDepth, isFloat, token).GetAwaiter().GetResult(); }
			catch (Exception ex) { Debug.WriteLine("RecordNoiseFloor failed: " + ex.Message); }
		}

		#endregion

		#region Record Step (ASIO / WASAPI)

		private const double RecordingTailSec = 0.5;

		private void RecordStepAsio(
			string sourceFile, string micFilePath, string? refFilePath,
			int sampleRate, int bitDepth, bool isFloat, int step,
			string? asioDriverName, CancellationToken token)
		{
			AudioFileReader? sourceReader = null;
			try
			{
				sourceReader = new AudioFileReader(sourceFile);
				ISampleProvider sp = sourceReader.ToSampleProvider();
				if (sp.WaveFormat.SampleRate != sampleRate) sp = new WdlResamplingSampleProvider(sp, sampleRate);
				if (sp.WaveFormat.Channels != 1) sp = sp.ToMono();

				using var asioRec = new AsioRecorder
				{
					DriverName = asioDriverName,
					SignalChannelIndex = _recorder.InputSignalChannel,
					UseReferenceChannel = _recorder.UseReferenceChannel,
					ReferenceChannelIndex = _recorder.InputReferenceChannel
				};
				asioRec.OnClipDetected += () => Debug.WriteLine("[ASIO] *** CLIPPING DETECTED ***");

				var tcs = new System.Threading.Tasks.TaskCompletionSource<string>(
					System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

				void Handler(string p) { try { asioRec.OnRecordingStopped -= Handler; } catch { } tcs.TrySetResult(p); }
				asioRec.OnRecordingStopped += Handler;

				try
				{
					asioRec.StartRecording(micFilePath, sampleRate, bitDepth, isFloat, sp,
						refFilePath: _recorder.UseReferenceChannel ? refFilePath : null);

					double fileDurationSec = _lastSyncLayout != null
						? (double)_lastSyncLayout.TotalLength / sampleRate
						: 10 + (double)SignalGenerator.GetSilenceDuration() * 2;
					int totalDurationMs = (int)((fileDurationSec + RecordingTailSec) * 1000);

					var delayTask = System.Threading.Tasks.Task.Delay(totalDurationMs, token);
					var completedTask = System.Threading.Tasks.Task.WhenAny(tcs.Task, delayTask).GetAwaiter().GetResult();
					if (completedTask == delayTask) { try { asioRec.StopRecording(); } catch { } }

					var timeoutTask = System.Threading.Tasks.Task.Delay(5000);
					if (System.Threading.Tasks.Task.WhenAny(tcs.Task, timeoutTask).GetAwaiter().GetResult() == timeoutTask)
						throw new TimeoutException("ASIO recording stop event did not fire");

					_ = tcs.Task.GetAwaiter().GetResult();
				}
				catch (Exception ex)
				{
					try { asioRec.OnRecordingStopped -= Handler; } catch { }
					Debug.WriteLine($"[ASIO] Recording failed: {ex.Message}");
					throw;
				}
			}
			finally { sourceReader?.Dispose(); }
		}

		private void RecordStepWasapi(
			string sourceFile, string micFilePath, string? refFilePath,
			MMDevice outputDevice, int sampleRate, int bitDepth, bool isFloat, int duration,
			MeasurementPlayback wasapiPlayback, CancellationToken token)
		{
			double fileDurationSec = _lastSyncLayout != null
				? (double)_lastSyncLayout.TotalLength / sampleRate
				: duration + (double)SignalGenerator.GetSilenceDuration() * 2;
			int recordDuration = (int)Math.Ceiling(fileDurationSec + RecordingTailSec);

			var recTask = RecordSegmentAsync(micFilePath, durationSeconds: recordDuration,
				sampleRate: sampleRate, bitDepth: bitDepth, isFloat: isFloat, token: token,
				refFilePath: _recorder.UseReferenceChannel ? refFilePath : null);

			wasapiPlayback.PlayFileBlocking(outputDevice, sourceFile, exclusive: false, token);
			_ = recTask.GetAwaiter().GetResult();
		}

		#endregion

		#region Step Analysis & Visualization

		private void ProcessStepResults(
			string micFilePath, string refFilePath, float angleDeg, int sampleRate,
			TestSignalType signalType, AnalysisMethod analysisMethod,
			List<int> selectedShowFrequencies, string imagesFolder, string spectrogramImagesFolder,
			string excelPath, int smoothingFractions = 0)
		{
			float[] recorded = AudioRecorder.ReadWavAsFloatMono(micFilePath, out int recSr);
			int sr = recSr > 0 ? recSr : sampleRate;

			float[] signalSlice = _lastSyncLayout?.SliceSignal(recorded, sr) ?? recorded;

			if (_recorder.UseReferenceChannel && File.Exists(refFilePath))
			{
				try { _analyzer.LoadFeedbackFromRecording(refFilePath, 0); }
				catch (Exception ex) { Debug.WriteLine($"[ProcessStep] WARNING: Cannot load feedback: {ex.Message}"); }
			}

			var fr = _analyzer.AnalyzeFrequencyResponse(recorded, sr, signalType, analysisMethod, signalSlice);
			var ampDbMap = BuildAmplitudeMap(fr, angleDeg);
			double localMaxDb = ampDbMap.Count > 0 ? ampDbMap.Values.Max() : double.MinValue;

			UpdateRawPolarData(angleDeg, selectedShowFrequencies, ampDbMap);

			if (localMaxDb > _globalMaxMagnitudeDb)
			{
				_globalMaxMagnitudeDb = localMaxDb;
				Debug.WriteLine($"[ProcessStep] *** NEW GLOBAL MAX: {_globalMaxMagnitudeDb:F2} dB at {angleDeg:F1}° ***");
			}

			// Update polar plot after each processed step
			if (_wpfPlot != null)
			{
				var normalizedData = GetNormalizedPolarData();
				Application.Current.Dispatcher.Invoke(() =>
				{
					try { ChartManagerScottPlot.SetData(normalizedData); }
					catch (Exception ex) { Debug.WriteLine($"[ProcessStep] Polar plot update failed: {ex.Message}"); }
				});
			}

			if (_fftPlot != null && signalType != TestSignalType.SteppedSine)
			{
				try
				{
					double octaveFraction = smoothingFractions > 0 ? 1.0 / smoothingFractions : 0.0;
					var fftResult = _analyzer.ComputeFrequencyResponse(
						recorded, sr, signalType, analysisMethod, signalSlice, octaveFraction);

					var preciseMags = fr.Select(p => (p.frequency, p.amplitudeDb)).ToList();
					UpdateFftPlot(angleDeg, signalType,
						fftResult.FrequencyAxis, fftResult.MagnitudeDb, preciseMags,
						normFreqLo: _analyzer.PlotF1, normFreqHi: _analyzer.PlotF2,
						imagesFolder: imagesFolder);
				}
				catch (Exception ex) { Debug.WriteLine($"[ProcessStep] FFT plot update failed: {ex.Message}"); }
			}

			if (_spectrogramPlot != null)
			{
				var spec = SignalAnalyzer.ComputeSpectrogram(recorded, sr, fftSize: 8192, hopSize: 1024);

				Application.Current.Dispatcher.Invoke(() =>
				{
					try
					{
						Spectrogram.ShowAndSaveSpectrogram(spec, sr, hopSize: 1024,
							baseFolder: spectrogramImagesFolder,
							fileName: $"Spectrogram_{angleDeg:0}deg.png",
							wpfPlot: _spectrogramPlot);
					}
					catch { }
				});
			}

			SaveStepToExcel(angleDeg, ampDbMap, signalType, recorded, sr, excelPath);

			if (signalType is TestSignalType.SineSweep or TestSignalType.MLS)
			{
				try
				{
					var refSig = _analyzer.GetOriginalSignal();
					var (imp, time) = _analyzer.GetImpulseResponse(recorded, refSig, sr);
					_excelSaver.AddImpulseResponseRow(angleDeg, imp);
					_excelSaver.AddTimeDomainBlock(angleDeg, time, imp);
				}
				catch { }
			}

			WriteStepWavMetadata(micFilePath, angleDeg, false);
			if (_recorder.UseReferenceChannel) WriteStepWavMetadata(refFilePath, angleDeg, true);
		}

		private Dictionary<int, double> BuildAmplitudeMap(
			List<(double frequency, double amplitudeDb)> fr, float angleDeg)
		{
			var ampDbMap = new Dictionary<int, double>();
			foreach (var (frequency, amplitudeDb) in fr)
			{
				int fInt = (int)Math.Round(frequency);
				if (!ampDbMap.ContainsKey(fInt)) ampDbMap[fInt] = amplitudeDb;
			}
			return ampDbMap;
		}

		private void UpdateRawPolarData(float angleDeg, List<int> selectedShowFrequencies,
			Dictionary<int, double> ampDbMap)
		{
			int angleInt = (int)Math.Round(angleDeg);
			foreach (var f in selectedShowFrequencies)
			{
				if (!_rawPolarData.TryGetValue(f, out var perAngle))
				{ perAngle = new SortedDictionary<int, double>(); _rawPolarData[f] = perAngle; }
				if (ampDbMap.TryGetValue(f, out double rawDbVal)) perAngle[angleInt] = rawDbVal;
			}
		}

		private void UpdateFftPlot(float angleDeg, TestSignalType signalType,
			double[] fftFreqs, double[] fftMags,
			List<(double frequency, double magnitudeDb)> preciseMags,
			double normFreqLo = 20.0, double normFreqHi = 20000.0,
			string? imagesFolder = null)
		{
			if (fftFreqs == null || fftMags == null || fftFreqs.Length < 2) return;

			double specPeak = double.MinValue;
			for (int i = 0; i < fftFreqs.Length; i++)
				if (fftFreqs[i] >= normFreqLo && fftFreqs[i] <= normFreqHi && fftMags[i] > specPeak)
					specPeak = fftMags[i];
			if (specPeak == double.MinValue)
			{
				for (int i = 0; i < fftMags.Length; i++)
					if (fftMags[i] > specPeak) specPeak = fftMags[i];
			}
			if (specPeak == double.MinValue) specPeak = 0.0;

			double[] fftMagsNorm = new double[fftMags.Length];
			for (int i = 0; i < fftMags.Length; i++) fftMagsNorm[i] = fftMags[i] - specPeak;

			var preciseMagsNorm = preciseMags
				.Select(p => (p.frequency, db: p.magnitudeDb - specPeak))
				.ToList();

			Application.Current.Dispatcher.Invoke(() =>
			{
				try
				{
					_fftPlot.Plot.Clear();
					var specLine = _fftPlot.Plot.Add.Scatter(fftFreqs, fftMagsNorm);
					specLine.Color = ScottPlot.Colors.DodgerBlue.WithAlpha(0.85);
					specLine.LineWidth = 1.5f;
					specLine.MarkerSize = 0;
					specLine.LegendText = "Spektrum";

					if (preciseMagsNorm.Count > 0)
					{
						var pts = _fftPlot.Plot.Add.Scatter(
							preciseMagsNorm.Select(p => p.frequency).ToArray(),
							preciseMagsNorm.Select(p => p.db).ToArray());
						pts.LineWidth = 0;
						pts.MarkerSize = 10;
						pts.MarkerShape = MarkerShape.FilledCircle;
						pts.Color = ScottPlot.Colors.Red;
						pts.LegendText = "Měřené body";
					}

					_fftPlot.Plot.Axes.AutoScale();
                    _fftPlot.Plot.Axes.Bottom.Label.Text = "Frekvence [Hz]";
					_fftPlot.Plot.Axes.Left.Label.Text = "Relativní amplituda [dB]";
					string suffix = signalType == TestSignalType.SteppedSine ? " (body = polární)" : "";
					_fftPlot.Plot.Title($"FFT Spektrum - {angleDeg:F1}°{suffix}");
					_fftPlot.Plot.ShowLegend();
					_fftPlot.Plot.Axes.SetLimitsY(-80.0, 3.0);
					_fftPlot.Plot.Axes.SetLimitsX(normFreqLo, normFreqHi);
					ScottPlotThemeHelper.ApplyTheme(_fftPlot, ThemeManager.Instance.IsDarkMode);
					_fftPlot.Refresh();

					if (!string.IsNullOrEmpty(imagesFolder))
						SaveFftPlot(imagesFolder, angleDeg);
				}
				catch (Exception ex) { Debug.WriteLine($"[UpdateFftPlot] Error: {ex.Message}"); }
			});
		}

		private void SaveStepToExcel(float angleDeg, Dictionary<int, double> ampDbMap,
			TestSignalType signalType, float[] recorded, int sr, string excelPath)
		{
			if (ampDbMap.Count == 0) return;
			_excelSaver.AddFrequencyResponseRawRow(angleDeg, ampDbMap);
			var normalizedMap = ampDbMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value - _globalMaxMagnitudeDb);
			_excelSaver.AddFrequencyResponseRow(angleDeg, normalizedMap);
		}

		private void WriteStepWavMetadata(string filePath, float angleDeg, bool isReference)
		{
			if (!File.Exists(filePath)) return;
			try
			{
				var tags = BuildWavTags(angleDeg, isReference);
				WavMetadata.WriteInfoChunk(filePath, tags);
			}
			catch (Exception ex) { Debug.WriteLine($"[WriteStepWavMetadata] {ex.Message}"); }
		}

		private Dictionary<string, string> BuildWavTags(float angleDeg, bool isReference)
		{
			string freqDescription = BuildFreqDescription();

			string channelType = isReference ? "reference (loopback)" : "mikrofon";
			string name = $"{_sessionSignalType} | {freqDescription} | {angleDeg:0.#}°";
			string comment =
				$"Přehrávané frekvence: {freqDescription} | " +
				$"Typ signálu: {_sessionSignalType} | " +
				$"Úhel: {angleDeg:0.#}° | " +
				$"Vzorkovací kmitočet: {_sessionSampleRate} Hz | " +
				$"Bitová hloubka: {_sessionBitDepth} bit | " +
				$"Driver: {_sessionDriverType} | " +
				$"Vzdálenost: {_sessionDistance} m | " +
				$"Mikrofon: {_sessionMicName} | " +
				$"Reproduktor: {_sessionSpeakerName} | " +
				$"Kanál: {channelType} | " +
				$"Datum: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

			return new Dictionary<string, string>
			{
				{ "INAM", name },
				{ "ICMT", comment },
				{ "IART", _sessionMicName },
				{ "ISRC", _sessionSpeakerName },
				{ "ISFT", "MeaSound" },
				{ "ICRD", DateTime.Now.ToString("yyyy-MM-dd") }
			};
		}

		private string BuildFreqDescription()
		{
			return _sessionSignalType switch
			{
				TestSignalType.SineSweep =>
					$"sweep {_sessionStartFreq} - {_sessionEndFreq} Hz",

				TestSignalType.MLS =>
					$"MLS {_sessionStartFreq} - {_sessionEndFreq} Hz",

				TestSignalType.ConstantTone =>
					$"{_sessionConstantFreq} Hz",

				TestSignalType.MultiTone when _sessionPlayMultipleFreqs is { Length: > 0 } =>
					string.Join(", ", _sessionPlayMultipleFreqs.Select(f => $"{f:0.#} Hz")),

				TestSignalType.SteppedSine when _sessionPlayMultipleFreqs is { Length: > 0 } =>
					string.Join(", ", _sessionPlayMultipleFreqs.Select(f => $"{f:0.#} Hz")),

				TestSignalType.WhiteNoise => "white noise",
				TestSignalType.PinkNoise => "pink noise",
				TestSignalType.CustomFile => "custom",

				_ => _sessionSignalType.ToString()
			};
		}

		#endregion

		#region Polar Data Normalization

		private Dictionary<int, SortedDictionary<int, double>> GetNormalizedPolarData()
		{
			var normalized = new Dictionary<int, SortedDictionary<int, double>>();
			foreach (var freqEntry in _rawPolarData)
			{
				normalized[freqEntry.Key] = new SortedDictionary<int, double>();
				foreach (var angleEntry in freqEntry.Value)
					normalized[freqEntry.Key][angleEntry.Key] = angleEntry.Value - _globalMaxMagnitudeDb;
			}
			return normalized;
		}

		#endregion

		#region RunMeasurement

		public void RunMeasurement(
			int repetitions,
			int stepsPerRevolution,
			int micAngle,
			AudioDeviceInfo outputDeviceInfo,
			AudioDeviceInfo inputDeviceInfo,
			TestSignalType signalType,
			List<int> selectedShowFrequencies,
			string micName,
			int sampleRate,
			int bitDepth,
			bool isFloat,
			string distanceMeters,
			bool recordNoise,
			AnalysisMethod analysisMethod = AnalysisMethod.Farina,
			float constantToneFreq = 1000f,
			SweepType sweepType = SweepType.Linear,
			int mlsOrder = 10,
			int duration = 10,
			int startFreq = 100,
			int endFreq = 8000,
			string baseSavePath = "",
			float[] playMultipleFreqs = null,
			PolarAxis polarAxis = null,
			double polarAxisRadius = 40,
			string customSoundFile = "",
			CancellationToken token = default,
			InputBackend inputBackend = InputBackend.Wasapi,
			string? asioDriverName = null,
			int captureChannelOverride = 0,
			int smoothingFractions = 3,
			string sessionSuffix = "")
		{
			if (token.IsCancellationRequested) return;

			int totalRepeats = Math.Max(1, repetitions);
			int stepsInSingleRepeat = stepsPerRevolution + 1;
			int totalPlannedSteps = totalRepeats * stepsInSingleRepeat;
			int completedSteps = 0;
			ReportStepProgress(completedSteps, totalPlannedSteps);
			ReportStatus("Inicializuji měření...");

			MMDevice? outputDevice = null;
			MMDevice? inputDevice = null;
			try
			{
				if (inputBackend != InputBackend.Asio) outputDevice = _audioDeviceManager.CreateOutputDeviceById(outputDeviceInfo?.Id);
				if (inputBackend == InputBackend.Wasapi) inputDevice = _audioDeviceManager.CreateInputDeviceById(inputDeviceInfo?.Id);
			}
			catch (Exception ex) { MessageBox.Show($"Chyba při otevírání audio zařízení: {ex.Message}"); ReportStatus("Chyba při otevírání audio zařízení."); return; }

			if (inputBackend != InputBackend.Asio && outputDevice == null) { MessageBox.Show("Chyba: Nebylo vybráno výstupní zařízení."); ReportStatus("Chybí výstupní zařízení."); return; }
			if (inputBackend == InputBackend.Wasapi && inputDevice == null) { MessageBox.Show("Chyba: Nebylo vybráno nahrávací zařízení."); ReportStatus("Chybí vstupní zařízení."); return; }
			if (!_serialManager.CheckDeviceConnection()) { MessageBox.Show("Zařízení není připojeno."); ReportStatus("Řídicí zařízení není připojeno."); return; }
			if (token.IsCancellationRequested) return;

			_recorder.Backend = inputBackend;
			_recorder.AsioDriverName = inputBackend == InputBackend.Asio ? asioDriverName : null;
			_recorder.CaptureChannelOverride = captureChannelOverride;
			if (inputBackend == InputBackend.Wasapi) { _recorder.SetDevice(inputDevice); _recorder.ReprobeExclusiveSupport(sampleRate, bitDepth, isFloat); }
			else _recorder.SetDevice(null);

			var generator = new SignalGenerator(sampleRate, 1);
			string sessionFolder = EnsureSessionFolder(baseSavePath, sessionSuffix);
			ReportStatus("Spouštím sekvenci měření...");

			using var wasapiPlayback = new MeasurementPlayback();
			using var asioPlayback = new AsioPlayback { DriverName = asioDriverName, OutputChannelIndex = 0 };

			try
			{
				string speakerName = inputBackend == InputBackend.Asio
					? (asioDriverName ?? "ASIO")
					: (outputDevice?.FriendlyName ?? outputDeviceInfo?.Name ?? "Neznámý reproduktor");

				_sessionMicName = micName;
				_sessionSpeakerName = speakerName;
				_sessionDistance = distanceMeters;
				_sessionSignalType = signalType;
				_sessionSampleRate = sampleRate;
				_sessionBitDepth = bitDepth;
				_sessionStartFreq = startFreq;
				_sessionEndFreq = endFreq;
				_sessionConstantFreq = constantToneFreq;
				_sessionPlayMultipleFreqs = playMultipleFreqs;
				_sessionDriverType = inputBackend == InputBackend.Asio ? "ASIO" : "WASAPI";

				for (int rep = 0; rep < totalRepeats; rep++)
				{
					if (token.IsCancellationRequested) break;
					ReportStatus($"Opakování {rep + 1}/{totalRepeats}: příprava měření...");

					if (rep > 0)
					{
						Application.Current.Dispatcher.Invoke(() =>
						{
							try { _spectrogramPlot.Plot.Clear(); _spectrogramPlot.Refresh(); } catch { }
						});
					}

					_globalMaxMagnitudeDb = double.MinValue;
					_rawPolarData.Clear();

					string timeStamp = DateTime.Now.ToString("HHmmss");
					string measurementFolder = Path.Combine(sessionFolder, $"Measurement_{_nextMeasurementNumber}_{timeStamp}");
					Directory.CreateDirectory(measurementFolder);
					_nextMeasurementNumber++;

					string audioFolder = Path.Combine(measurementFolder, "Audio");
					string imagesFolder = Path.Combine(measurementFolder, "Images");
					string spectrogramImagesFolder = Path.Combine(imagesFolder, "Spectrograms");
					Directory.CreateDirectory(audioFolder);
					Directory.CreateDirectory(imagesFolder);
					Directory.CreateDirectory(Path.Combine(imagesFolder, "Polar"));
					Directory.CreateDirectory(spectrogramImagesFolder);

					string sourceFile = GenerateTestSignal(
						signalType, generator, durationSeconds: duration, constantFreq: constantToneFreq,
						mlsOrder: mlsOrder, startFreq: startFreq, endFreq: endFreq,
						saveFolder: audioFolder, waveform: WaveformType.Sine, sweepType: sweepType,
						playMultipleFreqs: playMultipleFreqs, customSoundFile: customSoundFile,
						bitDepth: bitDepth, isFloat: isFloat, useReference: _recorder.UseReferenceChannel);

					if (token.IsCancellationRequested) break;

					ConfigureAnalyzer(signalType, sourceFile, sampleRate, startFreq, endFreq,
						duration, sweepType, selectedShowFrequencies, playMultipleFreqs);

					string excelPath = Path.Combine(measurementFolder, "MeasurementResults.xlsx");
					_excelSaver = new ExcelDataSaver();
					_excelSaver.Initialize(excelPath, selectedShowFrequencies);
					_excelSaver.TimeDecimationFactor = 10;
					_excelSaver.TimeMaxPointsPerAngle = 20000;
					_excelSaver.DriverType = inputBackend == InputBackend.Asio ? "ASIO" : "WASAPI";
					_excelSaver.SignalType = signalType;
					_excelSaver.DeconvolutionMethod = analysisMethod;
					_excelSaver.WienerLambda = WienerLambda;
					_excelSaver.SampleRate = sampleRate;
					_excelSaver.BitDepth = bitDepth;
					_excelSaver.MeasurementLengthSeconds = duration;
					_excelSaver.SweepStartFreqHz = startFreq;
					_excelSaver.SweepEndFreqHz = endFreq;
					_excelSaver.PlayedFrequencies = playMultipleFreqs ?? Array.Empty<float>();
					_excelSaver.PlayedFrequencyHz = constantToneFreq;
					double calibrationGainDb = Preferences.Load().CalibrationGainDb;
					_excelSaver.CreateInfoSheet(
						micName: micName, angle: micAngle, distance: distanceMeters,
						numRepeats: repetitions, speakerName: speakerName,
						stepsPerRev: stepsPerRevolution,
						calibrationGainDb: calibrationGainDb);

					if (recordNoise && !token.IsCancellationRequested)
					{
						ReportStatus($"Opakování {rep + 1}/{totalRepeats}: nahrávám šumové pozadí...");
						RecordNoiseFloor(audioFolder, 5, sampleRate, bitDepth, isFloat, token);
					}

					float degreesPerStep = (float)(360.0 / stepsPerRevolution);
					double currentMotorAngle = 0;

					for (int step = 0; step <= stepsPerRevolution; step++)
					{
						if (token.IsCancellationRequested) break;

						float relativeAngle = (step == 0) ? 0f : degreesPerStep;
						currentMotorAngle += relativeAngle;
						ReportStatus($"Opakování {rep + 1}/{totalRepeats}, krok {step + 1}/{stepsInSingleRepeat}: otáčím na {currentMotorAngle:0.#}°...");
						RotateToAngle(relativeAngle, token);

						string micFilePath = Path.Combine(audioFolder, $"Mic_{signalType}_{currentMotorAngle}deg_mic.wav");
						string refFilePath = Path.Combine(audioFolder, $"Mic_{signalType}_{currentMotorAngle}deg_ref.wav");

						try
						{
							ReportStatus($"Opakování {rep + 1}/{totalRepeats}, krok {step + 1}/{stepsInSingleRepeat}: nahrávám/přehrávám...");
							if (inputBackend == InputBackend.Asio)
								RecordStepAsio(sourceFile, micFilePath, refFilePath, sampleRate, bitDepth, isFloat, step, asioDriverName, token);
							else
								RecordStepWasapi(sourceFile, micFilePath, refFilePath, outputDevice!, sampleRate, bitDepth, isFloat, duration, wasapiPlayback, token);
						}
						catch (Exception ex)
						{
							ReportStatus($"Krok {step + 1}/{stepsInSingleRepeat}: chyba nahrávání - {ex.Message}");
							Debug.WriteLine("Record/Playback step failed: " + ex.Message);
							if (token.IsCancellationRequested) break;
						}

						if (File.Exists(micFilePath)) WriteStepWavMetadata(micFilePath, (float)currentMotorAngle, isReference: false);
						if (File.Exists(refFilePath)) WriteStepWavMetadata(refFilePath, (float)currentMotorAngle, isReference: true);

						if (!token.IsCancellationRequested)
						{
							try
							{
								ReportStatus($"Opakování {rep + 1}/{totalRepeats}, krok {step + 1}/{stepsInSingleRepeat}: analyzuji data...");
								ProcessStepResults(micFilePath, refFilePath, (float)currentMotorAngle, sampleRate,
									signalType, analysisMethod, selectedShowFrequencies, imagesFolder, spectrogramImagesFolder, excelPath, smoothingFractions);
								ReportStatus($"Opakování {rep + 1}/{totalRepeats}, krok {step + 1}/{stepsInSingleRepeat}: hotovo.");
							}
							catch (Exception ex)
							{
								ReportStatus($"Krok {step + 1}/{stepsInSingleRepeat}: chyba analýzy - {ex.Message}");
								Debug.WriteLine("Processing failed: " + ex.Message);
							}
						}

						completedSteps = Math.Min(totalPlannedSteps, completedSteps + 1);
						ReportStepProgress(completedSteps, totalPlannedSteps);
					}

					try { _excelSaver.Save(excelPath); } catch { }
					OnMeasurementProgress?.Invoke(rep + 1);

					if (!token.IsCancellationRequested)
						SavePolarPlot(imagesFolder, GetNormalizedPolarData());

					try { RotateToAngle((float)-currentMotorAngle, token); } catch (OperationCanceledException) { }
				}

				if (token.IsCancellationRequested)
					ReportStatus("Měření bylo zrušeno.");
				else
					ReportStatus("Měření bylo úspěšně dokončeno.");
			}
			catch (OperationCanceledException)
			{
				ReportStatus("Měření bylo zrušeno.");
			}
			catch (Exception ex)
			{
				ReportStatus($"Měření selhalo: {ex.Message}");
				Debug.WriteLine("RunMeasurement failed: " + ex.Message);
			}
			finally
			{
				try { inputDevice?.Dispose(); } catch { }
				try { outputDevice?.Dispose(); } catch { }
			}
		}

		#endregion

		#region Analyzer Configuration

		private void ConfigureAnalyzer(
			TestSignalType signalType, string sourceFile, int sampleRate,
			int startFreq, int endFreq, int duration, SweepType sweepType,
			List<int> selectedShowFrequencies, float[]? playMultipleFreqs)
		{
			_analyzer = new SignalAnalyzer();
			_analyzer.SetOriginalSignalFromFile(sourceFile);
			_analyzer.CurrentSweepType = sweepType;
			_analyzer.SweepStartFreq = startFreq;
			_analyzer.SweepEndFreq = endFreq;
			_analyzer.SweepDurationSeconds = duration;
			_analyzer.OriginalSignalSampleRate = sampleRate;
			_analyzer.WienerLambda = WienerLambda;

			if (signalType == TestSignalType.SteppedSine && playMultipleFreqs is { Length: > 0 })
				_analyzer.SetSelectedFrequencies(playMultipleFreqs.Select(f => (int)Math.Round(f)).ToList());
			else
				_analyzer.SetSelectedFrequencies(selectedShowFrequencies);

			_analyzer.UseFeedbackChannel = _recorder.UseReferenceChannel;
		}

		#endregion

		#region Signal Generation

		private string GenerateTestSignal(
			TestSignalType signalType, SignalGenerator generator,
			int durationSeconds, float constantFreq, int mlsOrder,
			int startFreq, int endFreq, float[] playMultipleFreqs, bool useReference,
			string saveFolder = "", string customSoundFile = "TestSignal.wav",
			WaveformType waveform = WaveformType.Sine, SweepType sweepType = SweepType.Linear,
			int bitDepth = 32, bool isFloat = true)
		{
			float[] samples = generator.GenerateSamples(
				type: signalType, durationSeconds: durationSeconds,
				constantFreq: constantFreq, mlsOrder: mlsOrder,
				startFreq: startFreq, endFreq: endFreq,
				waveform: waveform, sweepType: sweepType,
				multiToneFreqs: playMultipleFreqs, customFilePath: customSoundFile);

			bool isSweepSignal = signalType is TestSignalType.SineSweep or TestSignalType.MLS;

			float[] finalSamples;
			if (useReference)
			{
				finalSamples = generator.BuildReferenceBuffer(
					signal: samples, startFreq: startFreq, endFreq: endFreq,
					sweepType: sweepType, signalDurationSeconds: durationSeconds,
					isSweepSignal: isSweepSignal, layout: out SyncBufferLayout refLayout);
				_lastSyncLayout = refLayout;
			}
			else
			{
				finalSamples = generator.BuildSyncBuffer(
					signal: samples, startFreq: startFreq, endFreq: endFreq,
					sweepType: sweepType, signalDurationSeconds: durationSeconds,
					isSweepSignal: isSweepSignal, layout: out SyncBufferLayout syncLayout);
				_lastSyncLayout = syncLayout;
			}

			string testSignalPath = Path.Combine(saveFolder, "TestSignal.wav");
			SignalGenerator.SaveToWavFile(finalSamples, generator.SampleRate, 1, testSignalPath, bitDepth, isFloat);

			try
			{
				string freqInfo = signalType switch
				{
					TestSignalType.SineSweep => $"sweep {startFreq} - {endFreq} Hz",
					TestSignalType.MLS => $"MLS {startFreq} - {endFreq} Hz",
					TestSignalType.ConstantTone => $"{constantFreq:0.#} Hz",
					TestSignalType.MultiTone when playMultipleFreqs is { Length: > 0 }
						=> string.Join(", ", playMultipleFreqs.Select(f => $"{f:0.#} Hz")),
					TestSignalType.SteppedSine when playMultipleFreqs is { Length: > 0 }
						=> string.Join(", ", playMultipleFreqs.Select(f => $"{f:0.#} Hz")),
					TestSignalType.WhiteNoise => "white noise",
					TestSignalType.PinkNoise => "pink noise",
					_ => signalType.ToString()
				};
				WavMetadata.WriteInfoChunk(testSignalPath, new System.Collections.Generic.Dictionary<string, string>
				{
					{ "INAM", $"TestSignal | {signalType} | {freqInfo}" },
					{ "ICMT", $"Přehrávané frekvence: {freqInfo} | Typ: {signalType} | Vzorkovací kmitočet: {generator.SampleRate} Hz | Datum: {DateTime.Now:yyyy-MM-dd HH:mm:ss}" },
					{ "ISFT", "MeaSound" },
					{ "ICRD", DateTime.Now.ToString("yyyy-MM-dd") }
				});
			}
			catch (Exception ex) { Debug.WriteLine($"[GenerateTestSignal] WAV metadata: {ex.Message}"); }

			return testSignalPath;
		}

		#endregion

		#region Motor Control

		private void RotateToAngle(float angle, CancellationToken token)
			=> RotateToAngle(angle, 20000, token);

		private void RotateToAngle(float angle, int timeoutMs, CancellationToken token)
		{
			if (token.IsCancellationRequested) return;
			if (_serialManager?.Port == null || !_serialManager.Port.IsOpen)
				throw new InvalidOperationException("Zařízení není připojeno.");

			if (!_serialManager.CheckDeviceConnection())
				throw new InvalidOperationException("Zařízení přestalo odpovídat (ztráta spojení).");

			bool moveSucceeded;
			try
			{
				moveSucceeded = _serialManager
					.RotateMotorAsync(angle, timeoutMs)
					.WaitAsync(token)
					.GetAwaiter()
					.GetResult();
			}
			catch (OperationCanceledException)
			{
				throw;
			}

			if (moveSucceeded)
			{
				if (angle != 0f)
				{
					System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(1000), token)
						.GetAwaiter()
						.GetResult();
				}
				return;
			}

			bool deviceAlive = _serialManager.CheckDeviceConnection();
			throw deviceAlive
				? new TimeoutException($"Zařízení nereagovalo během {timeoutMs} ms při otáčení o {angle.ToString(System.Globalization.CultureInfo.InvariantCulture)}°.")
				: new InvalidOperationException("Zařízení přestalo odpovídat (ztráta spojení).");
		}

		#endregion

		#region Plot Export

		/// <summary>
		/// Saves the current FFT plot as PNG and SVG for the specified angle.
		/// </summary>
		private void SaveFftPlot(string imagesFolder, float angleDeg)
		{
			if (_fftPlot == null) return;
			try
			{
				string fftFolder = Path.Combine(imagesFolder, "FFT");
				Directory.CreateDirectory(fftFolder);

				bool saveWhite = Preferences.Load().SaveChartsWithWhiteBackground;
				bool isDarkMode = ThemeManager.Instance.IsDarkMode;
				var originalLimits = _fftPlot.Plot.Axes.GetLimits();

				if (saveWhite && isDarkMode)
					ScottPlotThemeHelper.ApplyTheme(_fftPlot.Plot, isDarkMode: false);

				try
				{
					_fftPlot.Plot.Axes.AutoScale();
					_fftPlot.Plot.SavePng(Path.Combine(fftFolder, $"FFT_{angleDeg:0}deg.png"), 1600, 900);
					try { _fftPlot.Plot.SaveSvg(Path.Combine(fftFolder, $"FFT_{angleDeg:0}deg.svg"), 1600, 900); } catch { }
				}
				finally
				{
					_fftPlot.Plot.Axes.SetLimits(originalLimits);
					if (saveWhite && isDarkMode)
						ScottPlotThemeHelper.ApplyTheme(_fftPlot, isDarkMode);
				}
			}
			catch (Exception ex) { Debug.WriteLine($"[SaveFftPlot] Error: {ex.Message}"); }
		}

		private void SavePolarPlot(string imagesFolder, Dictionary<int, SortedDictionary<int, double>> polarData)
		{
			if (_wpfPlot == null || polarData == null || polarData.Count == 0) return;
			try
			{
				string polarFolder = Path.Combine(imagesFolder, "Polar");
				Directory.CreateDirectory(polarFolder);
				Application.Current.Dispatcher.Invoke(() =>
				{
					try
					{
						bool saveWhite = Preferences.Load().SaveChartsWithWhiteBackground;
						bool wasDark = ChartManagerScottPlot.PrepareForSave(saveWhite);
						try
						{
							_wpfPlot.Plot.SavePng(Path.Combine(polarFolder, "PolarPlot_Final.png"), 1600, 1600);
							try { _wpfPlot.Plot.SaveSvg(Path.Combine(polarFolder, "PolarPlot_Final.svg"), 1600, 1600); } catch { }
						}
						finally { ChartManagerScottPlot.RestoreAfterSave(wasDark); }
					}
					catch (Exception ex) { Debug.WriteLine($"Polar plot save failed: {ex.Message}"); }
				});
			}
			catch (Exception ex) { Debug.WriteLine($"Polar folder creation failed: {ex.Message}"); }
		}

		#endregion
	}
}
