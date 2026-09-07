namespace DuckDeskPet.Core;

/// <summary>
/// Injectable entropy source for deterministic clip scheduling tests or a
/// host-provided random policy. Calls only occur when choosing an automatic
/// action after the fixed idle interval.
/// </summary>
public interface IClipRandomSource
{
    uint NextUInt32();
}
