using NWaves.Signals.Builders.Base;

namespace MeaSound
{
    /// <summary>
    /// Maximum Length Sequence (MLS) signal builder.
    /// </summary>
    public class MlsBuilder : SignalBuilder
    {
        private readonly int _order;
        private int _pos;
        private int[] _mls;

        public MlsBuilder(int order = 10)
        {
            _order = order;
            Reset();
        }

        public override float NextSample()
        {
            if (_pos >= _mls.Length) return 0f;
            return _mls[_pos++] == 1 ? 1.0f : -1.0f;
        }

        public override void Reset()
        {
            int length = (1 << _order) - 1;
            _mls = new int[length];
            int reg = (1 << _order) - 1;

            for (int i = 0; i < length; i++)
            {
                int bit = ((reg >> (_order - 1)) ^ (reg >> (_order - 2))) & 1;
                _mls[i] = (reg & 1) == 1 ? 1 : -1;
                reg = ((reg << 1) | bit) & ((1 << _order) - 1);
            }

            _pos = 0;
        }
    }
}
