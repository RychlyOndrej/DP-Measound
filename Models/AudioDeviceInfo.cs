namespace MeaSound
{
    /// <summary>
    /// Lightweight audio-device model used by UI selectors.
    /// </summary>
    public class AudioDeviceInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string FriendlyName => Name;
        public override string ToString() => Name;
    }
}
