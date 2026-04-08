# MeaSound – Polar Pattern Measurement

**MeaSound** is a WPF application designed for automated polar pattern measurements of microphones and other audio devices using an ESP32-controlled turntable. The software handles signal generation, audio recording, digital signal processing (DSP), and automatic exporting of results and graphs.

## 🌟 Key Features
- **Test Signals**: Sine Sweep (Linear / Exponential / Power-law), MLS, White/Pink noise, Multi-tone, Stepped sine, and Custom WAV files.
- **DSP Analysis**: Farina deconvolution (ESS), Wiener deconvolution (regularized spectral inversion), and Direct FFT.
- **Data Visualization**: Real-time Polar plots (ScottPlot), FFT spectrums, and STFT Spectrograms.
- **Audio Backend**: WASAPI & ASIO support, including a hardware loopback reference channel for latency compensation.
- **Export**: Automatically saves numerical data to Excel (.xlsx), charts to PNG/SVG, and all recordings to WAV.
- **UI**: Modern Dark/Light theme (MahApps.Metro + MaterialDesign).

## ⚙️ Requirements
* **OS**: Windows 10 / 11 (64-bit)
* **Framework**: .NET 9 (net9.0-windows)
* **Hardware**: 
  * ESP32 running serial communication firmware (turntable controller).
  * WASAPI or ASIO-compatible audio interface.

## 🚀 Quick Start
1. **Audio Setup**: Select your input/output devices and preferred backend. Enable the loopback reference channel if available.
2. **Signal & Analysis**: Choose a test signal (e.g., Exponential Sine Sweep) and the target frequency range.
3. **Turntable Setup**: Set the number of measurement steps per 360° rotation.
4. **Run**: Click **Start**. The app will automatically rotate the ESP32 turntable, play the signal, record the response, run the DSP analysis, and update the polar plot step-by-step.
5. **Results**: All generated WAV files, images, and the final Excel report are saved in a dedicated session folder.

---
---

# MeaSound – Měření polárních grafů (CZ)

## Popis aplikace
MeaSound je WPF aplikace pro automatizované měření polárních grafů mikrofonů a dalších audio zařízení řízeného přes ESP32. Aplikace generuje testovací signály, nahrává odezvu mikrofonu, analyzuje frekvenční charakteristiku a ukládá výsledky i WAV soubory.

### Hlavní funkce
- **Generování testovacích signálů** – Sine Sweep (lineární / exponenciální / power-law), MLS, bílý šum, růžový šum, konstantní tón, multi-tón, stepped sine a vlastní soubor.
- **Analýza frekvenční odezvy** – Farina dekonvoluce (ESS), Wiener dekonvoluce (regularizovaná spektrální inverze), přímé FFT.
- **Polární diagramy** – Automatické vykreslení polárního diagramu po každém kroku měření (ScottPlot).
- **FFT spektrum** – Zobrazení průběhu přenosové funkce i přesných bodů pro zvolené frekvence.
- **Spektrogram** – STFT vizualizace nahrávky (8192-bodové okno).
- **Smyčkový (loopback) referenční kanál** – Podpora druhého kanálu zvukové karty jako reference (WASAPI i ASIO).
- **Kompenzace mikrofonu** – Nastavitelný kalibrační zisk v dB.
- **Ukládání výsledků** – Excel (.xlsx přes ClosedXML), PNG/SVG grafy, WAV nahrávky.
- **Světlý / tmavý motiv** – MahApps.Metro + MaterialDesign, volitelné bílé pozadí pro export grafů.

---

## Požadavky
| Položka | Minimální verze |
|---|---|
| OS | Windows 10 / 11 (64-bit) |
| .NET | .NET 9 (net9.0-windows) |
| ESP32 | Firmware s podporou sériové komunikace (příkaz otočení, odpověď `rotated`) |
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
- **Výstupní zařízení** – Vyberte reproduktor / zesilovač, přes který se přehraje testovací signál.
- **Vstupní zařízení** – Vyberte mikrofon / zvukovou kartu pro nahrávání.
- **Backend** – Zvolte WASAPI (výchozí) nebo ASIO pro nízkolatenční měření.
- **Referenční kanál** – Pokud zvuková karta nabízí loopback kanál, povolte *Použít referenční kanál* pro přesnější dekonvoluci bez nutnosti zarovnávání signálů v čase.

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

- Nastavte **frekvenční rozsah** (např. 800 Hz – 8 kHz) a **délku sweepů** (např. 10 s).
- Zvolte **vzorkovací frekvenci** a **bitovou hloubku** podle možností zvukové karty.

### 3. Nastavení otočného stolku
- **Počet kroků na otáčku** – Počet měřicích poloh za jednu otočku (360°).
- **Počet opakování** – Kolikrát se celá otočka zopakuje (výsledky si vytvoří novou složku).
- **Úhel mikrofonu** – Vertikální poloha mikrofonu vůči ose reproduktoru.
- **Vzdálenost mikrofonu** – Vzdálenost mikrofonu od zdroje zvuku.

### 4. Volba analyzovaných frekvencí
- Vyberte frekvence, které se zobrazí jako body v polárním diagramu a v tabulce výsledků.

### 5. Spuštění měření
1. Klikněte na **Spustit měření**.
2. Aplikace vygeneruje testovací signál, uloží jej do session složky a zahájí sekvenci:
   - otočení stolku → přehrání signálu + simultánní nahrávání → analýza → aktualizace grafů.
3. Průběh je indikován progressbarem; měření lze kdykoli přerušit tlačítkem **Zastavit**.

### 6. Výsledky
Po dokončení měření je v session složce (`%BasePath%\MeaSound_YYYYMMDD_HHmmss\Measurement_N_HHMMSS`) k dispozici:

```text
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
