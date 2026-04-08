# MeaSound – Měření polárních grafů

## Popis aplikace
MeaSound je WPF aplikace pro automatizované měření polárních grafů mikroffonu a dalších audio zařízení zařízení řízeného ESP32. Aplikace generuje testovací signály, nahrává odezvu mikrofonu, analyzuje frekvenční charakteristiku a ukládá výsledky i wavky do souborů.

### Hlavní funkce
- **Generování testovacích signálů** – Sine Sweep (lineární / exponenciální / power-law), MLS, bílý šum, růžový šum, konstantní tón, multi-tón, stepped sine a vlastní soubor
- **Analýza frekvenční odezvy** – Farina dekonvoluce (ESS), Wiener dekonvoluce (regularizovaná spektrální inverze), přímé FFT
- **Polární diagramy** – automatické vykreslení polárního diagramu po každém kroku měření (ScottPlot)
- **FFT spektrum** – zobrazení průběhu přenosové funkce i přesných bodů pro zvolené frekvence
- **Spektrogram** – STFT vizualizace nahrávky (8192-bodové okno)
- **Smyčkový (loopback) referenční kanál** – podpora druhého kanálu zvukové karty jako reference (WASAPI i ASIO)
- **Kompenzace mikrofonu** – nastavitelný kalibrační zisk v dB
- **Ukládání výsledků** – Excel (.xlsx přes ClosedXML), PNG/SVG grafy, WAV nahrávky
- **Světlý / tmavý motiv** – MahApps.Metro + MaterialDesign, volitelné bílé pozadí pro export grafů

---

## Požadavky
| Položka | Minimální verze |
|---|---|
| OS | Windows 10 / 11 (64-bit) |
| .NET | .NET 9 (net9.0-windows) |
| ESP32 | firmware s podporou sériové komunikace (příkaz otočení, odpověď `rotated`) |
| Zvuková karta | WASAPI nebo ASIO-kompatibilní zařízení |

---

## Závislosti (NuGet)
| Balíček | Verze |
|---|---|
| ClosedXML | 0.105.0 |
| DocumentFormat.OpenXml | 3.3.0 |
| MahApps.Metro | 2.4.11 |
| MaterialDesignThemes | 5.3.0 |
| MathNet.Numerics | 5.0.0 |
| NAudio | 2.2.1 |
| NWaves | 0.9.6 |
| OxyPlot.Wpf | 2.2.0 |
| ScottPlot | 5.1.57 |
| ScottPlot.WPF | 5.1.57 |
| System.IO.Ports | 10.0.1 |
| System.Management | 10.0.1 |

---

## Použití aplikace

### 1. Nastavení audio zařízení
- **Výstupní zařízení** – vyberte reproduktor / zesilovač, přes který se přehraje testovací signál.
- **Vstupní zařízení** – vyberte mikrofon / zvukovou kartu pro nahrávání.
- **Backend** – zvolte WASAPI (výchozí) nebo ASIO pro nízkolatenční měření.
- **Referenční kanál** – pokud zvuková karta nabízí loopback kanál, povolte *Použít referenční kanál* pro přesnější dekonvoluci bez nutnosti zarovnávání signálů.

### 2. Výběr testovacího signálu
| Typ | Popis | Doporučená analýza |
|---|---|---|
| Sine Sweep (Exponential) | ESS – 6 dB/okt. nárůst energie | Farina |
| Sine Sweep (Linear / PowerLaw) | Lineární nebo power-law průběh | Wiener |
| MLS | Maximální délková sekvence | Wiener |
| Stepped Sine | Diskrétní frekvence, jedna po druhé | DirectFFT |
| White / Pink Noise | Širokopásmový šum | DirectFFT |
| Multi-tón / Konstantní tón | Vybrané frekvence najednou / jedna | DirectFFT |
| Vlastní soubor | Libovolný WAV soubor | DirectFFT / Wiener |

- Nastavte **frekvenční rozsah** (výchozí 100 Hz – 8 kHz) a **délku sweepů** (výchozí 10 s).
- Zvolte **vzorkovací frekvenci** a **bitovou hloubku** podle možností zvukové karty.

### 3. Nastavení otočného stolku
- **Počet kroků na otáčku** – počet měřicích poloh za jednu otočku (360°).
- **Počet opakování** – kolikrát se celá otočka zopakuje a výsledky zprůměrují.
- **Úhel mikrofonu** – výchozí poloha mikrofonu vůči ose reproduktoru.
- **Vzdálenost mikrofonu** – vzdálenost mikrofonu od zdroje zvuku (zaznamenává se do Excelu).

### 4. Volba analyzovaných frekvencí
- Zadejte seznam frekvencí, které se zobrazí jako body v polárním diagramu a v tabulce výsledků.

### 5. Spuštění měření
1. Klikněte na **Spustit měření**.
2. Aplikace vygeneruje testovací signál, uloží jej do session složky a zahájí sekvenci:
   - otočení stolku → přehrání signálu + simultánní nahrávání → analýza → aktualizace grafů.
3. Průběh je indikován progressbarem; měření lze kdykoli přerušit tlačítkem **Zastavit**.

### 6. Výsledky
Po dokončení měření je v session složce (`%BasePath%\MeaSound_YYYYMMDD_HHmmss\Measurement_N_HHMMSS`) k dispozici:

```
Measurement_1_143020/
├── Audio/
│   ├── TestSignal.wav          # vygenerovaný testovací signál
│   ├── Mic_SineSweep_0deg_mic.wav
│   └── ...
├── Images/
│   ├── Polar/
│   │   ├── PolarPlot_Final.png
│   │   └── PolarPlot_Final.svg
│   ├── FFT/
│   │   ├── FFT_0deg.png
│   │   └── ...
│   └── Spectrograms/
│       ├── Spectrogram_0deg.png
│       └── ...
└── MeasurementResults.xlsx     # frekvenční odezva, impulsní odezva, časová doména
```

### 7. Kalibrace výstupu
- Otevřete **Kalibrace výstupu** (ikona v hlavním panelu).
- Nastavte kalibrační zisk v dB; hodnota se uloží do `%AppData%\MeaSound\preferences.json` a aplikuje se na veškeré přehrávání.

---

## Architektura projektu

```
MeaSound/
├── Analysis/
│   ├── SignalAnalyzer.cs       # DSP jádro: FFT, dekonvoluce, IR, THD, SNR …
│   ├── SignalGenerator.cs      # generování testovacích signálů
│   └── Spectrogram.cs          # STFT vizualizace
├── Audio/
│   ├── AudioRecorder.cs        # WASAPI nahrávání
│   ├── AsioRecorder.cs         # ASIO nahrávání
│   ├── AsioPlayback.cs         # ASIO přehrávání
│   ├── MeasurementPlayback.cs  # WASAPI přehrávání pro měření
│   └── AudioDeviceManager.cs   # výčet a správa audio zařízení
├── Measurement/
│   ├── MeasurementManager.cs   # orchestrace celého měřicího procesu
│   └── ExcelDataSaver.cs       # export do .xlsx
├── Serial/
│   └── SerialPortManager.cs    # komunikace s ESP32
├── UI/
│   ├── MainWindow/             # partial třídy hlavního okna
│   ├── ChartManager.cs         # správa ScottPlot grafů
│   └── ThemeManager.cs         # správa světlého/tmavého motivu
├── Config/
│   └── Preferences.cs          # trvalá nastavení (JSON)
├── Enums/
│   └── SignalEnums.cs           # výčty: TestSignalType, SweepType, AnalysisMethod …
└── Helpers/                    # pomocné třídy (ArraySampleProvider, MlsBuilder …)
```

---

## Autor
Vyvinuto v rámci projektu automatizovaného měření polárních grafů s otočným zařízením (ESP+krokmotor).

## Licence
Tento software je poskytován „tak jak je" bez jakýchkoli záruk.

