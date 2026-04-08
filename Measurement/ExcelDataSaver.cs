using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Office2010.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace MeaSound
{
    internal class ExcelDataSaver
    {
        private SpreadsheetDocument? document;
        private WorkbookPart? workbookPart;
        private Sheets? sheetsCollection;

        private Dictionary<string, WorksheetPart> worksheetParts = new();
        private Dictionary<string, OpenXmlWriter> writers = new();
        private Dictionary<string, uint> currentRow = new();

        private List<int> selectedFrequenciesHz = new();
        private string? outputPath;
        private string? tempFolderPath;

        private List<string> phaseAngleKeys = new();
        private Dictionary<string, string> phaseFiles = new();
        private List<string> timeAngleKeys = new();
        private Dictionary<string, string> timeFiles = new();

        public int TimeDecimationFactor { get; set; } = 1;
        public int? TimeMaxPointsPerAngle { get; set; } = null;

        public string DriverType { get; set; } = string.Empty;
        public TestSignalType SignalType { get; set; } = TestSignalType.SineSweep;
        public int SampleRate { get; set; }
        public int BitDepth { get; set; }
        public double MeasurementLengthSeconds { get; set; }
        public int SweepStartFreqHz { get; set; }
        public int SweepEndFreqHz { get; set; }
        public float[] PlayedFrequencies { get; set; } = Array.Empty<float>();
        public float PlayedFrequencyHz { get; set; }
        public AnalysisMethod DeconvolutionMethod { get; set; } = AnalysisMethod.DirectFft;
        public double WienerLambda { get; set; } = 1e-5;

        public ExcelDataSaver() { }

        public void Initialize(string excelFilePath, List<int> frequenciesHz)
        {
            outputPath = excelFilePath;
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }

            document = SpreadsheetDocument.Create(outputPath, SpreadsheetDocumentType.Workbook);
            workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            sheetsCollection = workbookPart.Workbook.AppendChild(new Sheets());
            selectedFrequenciesHz = frequenciesHz;

            tempFolderPath = Path.Combine(Path.GetTempPath(), "MeaSound_ExcelCache_" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(tempFolderPath); } catch { tempFolderPath = Path.GetTempPath(); }
        }

        private void EnsureSheetWriter(string sheetName)
        {
            if (writers.ContainsKey(sheetName)) return;

            var wsPart = workbookPart.AddNewPart<WorksheetPart>();
            var writer = OpenXmlWriter.Create(wsPart);
            writer.WriteStartElement(new Worksheet());
            writer.WriteStartElement(new SheetData());

            worksheetParts[sheetName] = wsPart;
            writers[sheetName] = writer;
            currentRow[sheetName] = 0;

            uint sheetId = (uint)(sheetsCollection.Count() + 1);
            sheetsCollection.Append(new Sheet() { Id = workbookPart.GetIdOfPart(wsPart), SheetId = sheetId, Name = sheetName });
        }

        private void WriteRow(string sheetName, params object[] values)
        {
            EnsureSheetWriter(sheetName);
            var writer = writers[sheetName];
            uint rowIndex = ++currentRow[sheetName];
            writer.WriteStartElement(new Row { RowIndex = rowIndex });

            foreach (var val in values)
            {
                if (val == null) { writer.WriteElement(new Cell()); continue; }
                switch (val)
                {
                    case string s:   writer.WriteElement(new Cell(new InlineString(new Text(s))) { DataType = CellValues.InlineString }); break;
                    case int i:      writer.WriteElement(new Cell(new CellValue(i.ToString(CultureInfo.InvariantCulture))) { DataType = CellValues.Number }); break;
                    case float f:    writer.WriteElement(new Cell(new CellValue(f.ToString(CultureInfo.InvariantCulture))) { DataType = CellValues.Number }); break;
                    case double d:   writer.WriteElement(new Cell(new CellValue(d.ToString(CultureInfo.InvariantCulture))) { DataType = CellValues.Number }); break;
                    case long l:     writer.WriteElement(new Cell(new CellValue(l.ToString(CultureInfo.InvariantCulture))) { DataType = CellValues.Number }); break;
                    case decimal dec:writer.WriteElement(new Cell(new CellValue(dec.ToString(CultureInfo.InvariantCulture))) { DataType = CellValues.Number }); break;
                    case DateTime dt:writer.WriteElement(new Cell(new InlineString(new Text(dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))) { DataType = CellValues.InlineString }); break;
                    default:         writer.WriteElement(new Cell(new InlineString(new Text(val.ToString()))) { DataType = CellValues.InlineString }); break;
                }
            }

            writer.WriteEndElement(); // Row
        }

        public void CreateInfoSheet(string micName, string speakerName, string distance, int stepsPerRev, float angle, int numRepeats, double calibrationGainDb)
        {
            string sheetName = "Info";
            WriteRow(sheetName, "Mìøení measound ", DateTime.Now.ToString("yyyy-MM-dd"));
            WriteRow(sheetName, "");
            WriteRow(sheetName, "Typ driveru", DriverType);
            WriteRow(sheetName, "Mikrofon", micName);
            WriteRow(sheetName, "Reproduktor", speakerName);
            WriteRow(sheetName, "");
            WriteRow(sheetName, "Typ signálu", SignalType.ToString());
            WriteRow(sheetName, "Typ dekonvoluce", GetDeconvolutionMethodLabel(DeconvolutionMethod, WienerLambda));
            WriteRow(sheetName, "Kalibrace [dB]", calibrationGainDb);
            WriteRow(sheetName, "");
            WriteRow(sheetName, "Vzorkovací frekvence [Hz]", SampleRate);
            WriteRow(sheetName, "Bitová hloubka [bit]", BitDepth);
            WriteRow(sheetName, "Délka mìøení [s]", MeasurementLengthSeconds);

            if (SignalType == TestSignalType.SineSweep)
            {
                WriteRow(sheetName, "Frekvenèní rozsah [Hz]", $"{SweepStartFreqHz} - {SweepEndFreqHz}");
            }
            else if (SignalType == TestSignalType.MultiTone)
            {
                WriteRow(sheetName, "Frekvence tónù [Hz]", string.Join(", ", PlayedFrequencies));
            }
            else if (SignalType == TestSignalType.SteppedSine || SignalType == TestSignalType.ConstantTone)
            {
                WriteRow(sheetName, "Hraná frekvence [Hz]",
                    PlayedFrequencies.Length > 1
                        ? string.Join(", ", PlayedFrequencies)
                        : PlayedFrequencyHz.ToString(CultureInfo.InvariantCulture));
            }
            WriteRow(sheetName, "");
            WriteRow(sheetName, "Vzdálenost [m]", distance);
            WriteRow(sheetName, "Úhel mìøení [°]", angle);
            WriteRow(sheetName, "Pootoèení reproduktoru [°]", stepsPerRev);
            WriteRow(sheetName, "Poèet opakování", numRepeats);
            WriteRow(sheetName, "");
        }

        private static string GetDeconvolutionMethodLabel(AnalysisMethod method, double wienerLambda)
        {
            return method switch
            {
                AnalysisMethod.Wiener => $"Wiener (? = {wienerLambda.ToString("G6", CultureInfo.InvariantCulture)})",
                AnalysisMethod.Farina => "Farina",
                AnalysisMethod.DirectFft => "FFT",
                _ => method.ToString()
            };
        }

        public void AddFrequencyResponseRow(float angleDeg, Dictionary<int, double> amplitudesDbForFreq)
        {
            string sheetName = "Frequency";
            if ((currentRow.ContainsKey(sheetName) ? currentRow[sheetName] : 0) == 0)
                WriteRow(sheetName, "Frekvence (Hz)", "Úhel (°)", "Amplituda (dB norm)");

            foreach (var freq in selectedFrequenciesHz)
            {
                double ampDb = amplitudesDbForFreq != null && amplitudesDbForFreq.ContainsKey(freq) ? amplitudesDbForFreq[freq] : 0.0;
                if (double.IsNaN(ampDb) || double.IsInfinity(ampDb)) ampDb = 0;
                WriteRow(sheetName, freq, angleDeg, ampDb);
            }
        }

        public void AddFrequencyResponseRawRow(float angleDeg, Dictionary<int, double> amplitudesDbForFreq)
        {
            string sheetName = "Frequency_RAW";
            if ((currentRow.ContainsKey(sheetName) ? currentRow[sheetName] : 0) == 0)
                WriteRow(sheetName, "Frekvence (Hz)", "Úhel (°)", "Amplituda RAW (dB)");

            foreach (var freq in selectedFrequenciesHz)
            {
                double ampDb = amplitudesDbForFreq != null && amplitudesDbForFreq.ContainsKey(freq) ? amplitudesDbForFreq[freq] : 0.0;
                if (double.IsNaN(ampDb) || double.IsInfinity(ampDb)) ampDb = 0;
                WriteRow(sheetName, freq, angleDeg, ampDb);
            }
        }

        public void AddPhaseResponseRow(float angleDeg, Dictionary<int, double> phaseResponse)
        {
            string key = AngleKey(angleDeg);
            if (!phaseAngleKeys.Contains(key))
            {
                phaseAngleKeys.Add(key);
                phaseFiles[key] = Path.Combine(tempFolderPath, $"Phase_{key}.csv");
            }
            try
            {
                using var sw = new StreamWriter(phaseFiles[key], append: true);
                foreach (var kvp in phaseResponse.OrderBy(k => k.Key))
                {
                    double val = double.IsNaN(kvp.Value) || double.IsInfinity(kvp.Value) ? 0 : kvp.Value;
                    sw.WriteLine($"{kvp.Key};{val.ToString(CultureInfo.InvariantCulture)}");
                }
            }
            catch (Exception ex) { Debug.WriteLine("Error writing phase temp file: " + ex.Message); }
        }

        public void AddThdRow(float angleDeg, double thd)
        {
            string sheetName = "THD";
            if ((currentRow.ContainsKey(sheetName) ? currentRow[sheetName] : 0) == 0)
                WriteRow(sheetName, "Úhel (°)", "THD (%)");
            double val = double.IsNaN(thd) || double.IsInfinity(thd) ? 0 : thd;
            WriteRow(sheetName, angleDeg, val);
        }

        public void AddTimeDomainBlock(float angleDeg, double[] timeAxis, float[] amplitudes)
        {
            string key = AngleKey(angleDeg);
            if (!timeAngleKeys.Contains(key))
            {
                timeAngleKeys.Add(key);
                timeFiles[key] = Path.Combine(tempFolderPath, $"Time_{key}.csv");
            }
            int decim = Math.Max(1, TimeDecimationFactor);
            int maxPoints = TimeMaxPointsPerAngle.HasValue ? Math.Max(0, TimeMaxPointsPerAngle.Value) : int.MaxValue;
            try
            {
                using var sw = new StreamWriter(timeFiles[key], append: true);
                int len = Math.Min(timeAxis?.Length ?? 0, amplitudes?.Length ?? 0);
                int written = 0;
                for (int i = 0; i < len; i += decim)
                {
                    if (written >= maxPoints) break;
                    double amp = double.IsNaN(amplitudes[i]) || double.IsInfinity(amplitudes[i]) ? 0 : amplitudes[i];
                    sw.WriteLine($"{timeAxis[i].ToString(CultureInfo.InvariantCulture)};{amp.ToString(CultureInfo.InvariantCulture)}");
                    written++;
                }
            }
            catch (Exception ex) { Debug.WriteLine("Error writing time temp file: " + ex.Message); }
        }

        public void AddImpulseResponseRow(float angleDeg, float[] impulseResponse)
        {
            string sheetName = "Impulse";
            if ((currentRow.ContainsKey(sheetName) ? currentRow[sheetName] : 0) == 0)
                WriteRow(sheetName, "Úhel (°)", "Vzorek (index)", "Amplituda");
            for (int i = 0; i < impulseResponse.Length; i++)
            {
                double v = double.IsNaN(impulseResponse[i]) || double.IsInfinity(impulseResponse[i]) ? 0 : impulseResponse[i];
                WriteRow(sheetName, angleDeg, i, v);
            }
        }

        public void AddHarmonicsRow(float angleDeg, Dictionary<double, double> harmonics)
        {
            string sheetName = "Harmonics";
            if ((currentRow.ContainsKey(sheetName) ? currentRow[sheetName] : 0) == 0)
                WriteRow(sheetName, "Úhel (°)", "Frekvence (Hz)", "Amplituda");
            foreach (var kvp in harmonics)
            {
                double val = double.IsNaN(kvp.Value) || double.IsInfinity(kvp.Value) ? 0 : kvp.Value;
                WriteRow(sheetName, angleDeg, kvp.Key, val);
            }
        }

        private string AngleKey(float angleDeg)
        {
            if (Math.Abs(angleDeg - Math.Round(angleDeg)) < 1e-6)
                return ((int)Math.Round(angleDeg)).ToString(CultureInfo.InvariantCulture);
            return angleDeg.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', '_');
        }

        private void WriteAnglePairedSheet(string sheetName, List<string> angleKeys, Dictionary<string, string> files, string firstColHeader, string secondColHeader)
        {
            if (angleKeys == null || angleKeys.Count == 0) return;

            var wsPart = workbookPart.AddNewPart<WorksheetPart>();
            using var writer = OpenXmlWriter.Create(wsPart);
            writer.WriteStartElement(new Worksheet());
            writer.WriteStartElement(new SheetData());

            writer.WriteStartElement(new Row { RowIndex = 1 });
            foreach (var key in angleKeys)
            {
                writer.WriteElement(new Cell(new InlineString(new Text(key.Replace('_', '.')))) { DataType = CellValues.InlineString });
                writer.WriteElement(new Cell());
            }
            writer.WriteEndElement();

            writer.WriteStartElement(new Row { RowIndex = 2 });
            foreach (var _ in angleKeys)
            {
                writer.WriteElement(new Cell(new InlineString(new Text(firstColHeader))) { DataType = CellValues.InlineString });
                writer.WriteElement(new Cell(new InlineString(new Text(secondColHeader))) { DataType = CellValues.InlineString });
            }
            writer.WriteEndElement();

            var readers = new StreamReader?[angleKeys.Count];
            try
            {
                for (int i = 0; i < angleKeys.Count; i++)
                    if (files.TryGetValue(angleKeys[i], out var path) && File.Exists(path))
                        readers[i] = new StreamReader(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

                uint rowIndex = 3;
                while (true)
                {
                    bool any = false;
                    writer.WriteStartElement(new Row { RowIndex = rowIndex });

                    for (int i = 0; i < readers.Length; i++)
                    {
                        var sr = readers[i];
                        if (sr == null) { writer.WriteElement(new Cell()); writer.WriteElement(new Cell()); continue; }
                        string? line = sr.ReadLine();
                        if (line == null || string.IsNullOrWhiteSpace(line)) { writer.WriteElement(new Cell()); writer.WriteElement(new Cell()); continue; }
                        any = true;
                        var parts = line.Split(';');
                        string cellA = parts.Length >= 1 ? parts[0] : string.Empty;
                        string cellB = parts.Length >= 2 ? parts[1] : string.Empty;

                        if (double.TryParse(cellA, NumberStyles.Any, CultureInfo.InvariantCulture, out double numA))
                            writer.WriteElement(new Cell(new CellValue(numA.ToString(CultureInfo.InvariantCulture))) { DataType = CellValues.Number });
                        else
                            writer.WriteElement(new Cell(new InlineString(new Text(cellA))) { DataType = CellValues.InlineString });

                        if (double.TryParse(cellB, NumberStyles.Any, CultureInfo.InvariantCulture, out double numB))
                            writer.WriteElement(new Cell(new CellValue(numB.ToString(CultureInfo.InvariantCulture))) { DataType = CellValues.Number });
                        else
                            writer.WriteElement(new Cell(new InlineString(new Text(cellB))) { DataType = CellValues.InlineString });
                    }

                    writer.WriteEndElement(); // Row
                    if (!any) break;
                    rowIndex++;
                }
            }
            finally { for (int i = 0; i < readers.Length; i++) { try { readers[i]?.Dispose(); } catch { } } }

            writer.WriteEndElement(); // SheetData
            writer.WriteEndElement(); // Worksheet
            uint sheetId = (uint)(sheetsCollection.Count() + 1);
            sheetsCollection.Append(new Sheet() { Id = workbookPart.GetIdOfPart(wsPart), SheetId = sheetId, Name = sheetName });
        }

        public void Save(string saveFilePath)
        {
            foreach (var kv in writers)
            {
                try { var w = kv.Value; w.WriteEndElement(); w.WriteEndElement(); w.Close(); }
                catch (Exception ex) { Debug.WriteLine("Error closing writer for sheet " + kv.Key + ": " + ex.Message); }
            }
            writers.Clear();

            try { WriteAnglePairedSheet("Phase", phaseAngleKeys, phaseFiles, "Frekvence (Hz)", "Fáze (°)"); } catch (Exception ex) { Debug.WriteLine("Error writing phase sheet: " + ex.Message); }
            try { WriteAnglePairedSheet("Time", timeAngleKeys, timeFiles, "Èas (s)", "Amplituda"); } catch (Exception ex) { Debug.WriteLine("Error writing time sheet: " + ex.Message); }

            try { workbookPart?.Workbook.Save(); document?.Dispose(); document = null; workbookPart = null; sheetsCollection = null; }
            catch (Exception ex) { Debug.WriteLine("Error saving workbook: " + ex.Message); }

            try { if (!string.IsNullOrEmpty(tempFolderPath) && Directory.Exists(tempFolderPath)) Directory.Delete(tempFolderPath, true); } catch { }

            if (!string.IsNullOrEmpty(saveFilePath) && !string.Equals(saveFilePath, outputPath, StringComparison.OrdinalIgnoreCase))
            {
                try { if (File.Exists(saveFilePath)) File.Delete(saveFilePath); File.Move(outputPath, saveFilePath); }
                catch (Exception ex) { Debug.WriteLine("Error moving saved file: " + ex.Message); }
            }
        }
    }
}
