namespace DuckDeskPet.Core;

internal sealed class XorShiftClipRandomSource : IClipRandomSource
{
    private uint _state;

    public XorShiftClipRandomSource(uint seed)
    {
        _state = seed == 0 ? 0xA341_316Cu : seed;
    }

    public uint NextUInt32()
    {
        uint value = _state;
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        _state = value;
        return value;
    }
}
