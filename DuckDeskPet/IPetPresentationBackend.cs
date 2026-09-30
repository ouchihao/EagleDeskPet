using System.Windows.Controls;
using DuckDeskPet.Core;

namespace DuckDeskPet;

internal sealed record PresentationEquipment(string Desk, string Computer, string Outfit);

internal interface IPetPresentationBackend : IDisposable
{
    bool IsActive { get; }
    bool IsPreparing { get; }
    string? Warning { get; }
    PresentationEquipment? ActiveEquipment { get; }
    event Action? PresentationChanged;
    bool CanAdvance(ClipSample sample);
    void Present(ClipSample sample, bool paused, bool force = false);
}

internal delegate Task<IPetPresentationBackend?> PreviewPresentationFactory(Grid surface,
    PresentationEquipment equipment, ClipSample sample, CancellationToken cancellationToken);
