namespace DuckDeskPet.Core;

/// <summary>
/// Injectable entropy source for deterministic clip scheduling tests or a
/// host-provided random policy, consumed once when scheduling each idle delay
/// </summary>
public interface IClipRandomSource
{
    uint NextUInt32();
}
