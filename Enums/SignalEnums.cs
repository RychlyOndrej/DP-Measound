namespace MeaSound
{
    /// <summary>
    /// Measurement test signal type.
    /// </summary>
    public enum TestSignalType
    {
        SineSweep,
        MLS,
        WhiteNoise,
        PinkNoise,
        ConstantTone,
        MultiTone,
        SteppedSine,
        CustomFile
    }

    /// <summary>
    /// Sweep frequency progression type.
    /// </summary>
    public enum SweepType
    {
        /// <summary>Linear sweep with uniform frequency increase over time.</summary>
        Linear,

        /// <summary>Exponential sweep (ESS) with logarithmic frequency spacing.</summary>
        ExponentialSweep,

        /// <summary>Power-law sweep with polynomial frequency progression.</summary>
        PowerLaw,
    }

    /// <summary>
    /// Waveform type used for tone generation.
    /// </summary>
    public enum WaveformType
    {
        Sine,
        Square,
        Triangle,
        Sawtooth
    }

    /// <summary>
    /// Serial communication state with the controller.
    /// </summary>
    public enum SerialConnectionState
    {
        Ok,
        Communicating,
        Error
    }

    /// <summary>
    /// Audio input backend.
    /// </summary>
    public enum InputBackend
    {
        Wasapi,
        Asio
    }

    /// <summary>
    /// Frequency-response analysis method.
    /// </summary>
    public enum AnalysisMethod
    {
        /// <summary>Farina deconvolution for exponential sweep without loopback.</summary>
        Farina,

        /// <summary>Wiener regularized spectral inversion.</summary>
        Wiener,

        /// <summary>Direct FFT without deconvolution.</summary>
        DirectFft,

        /// <summary>Sentinel value for canceled method selection.</summary>
        Cancelled,
    }
}
