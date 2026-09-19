using System.IO;
using System.Windows;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    private readonly Dictionary<string, bool> _contentResources = new(StringComparer.Ordinal);
    private bool _preparingOwnedAction, _outfitApplying;
    private bool _transientResourcesWereInUse;
    private static readonly ClipKind[] TransientContentClips = [ClipKind.RpsRock, ClipKind.RpsPaper, ClipKind.RpsScissors, ClipKind.RpsWin, ClipKind.RpsLose, ClipKind.Tea];

    private void ReleaseUnusedTransientResources()
    {
        if (IsGameActive || _preparingOwnedAction) { _transientResourcesWereInUse = true; return; }
        if (!_transientResourcesWereInUse || _behavior.CurrentSample.Kind != ClipKind.Idle || _behavior.PendingCount != 0 || IsContentEquipmentApplying) return;
        _framePlayer.ReleaseClips(TransientContentClips);
        _transientResourcesWereInUse = false;
    }
    internal bool IsContentInteractionBusy => IsGameActive || _preparingReaction || _preparingOwnedAction || _outfitApplying || IsContentEquipmentApplying;
    internal bool IsContentResourceAvailable(ContentDefinition item) => _contentResources.GetValueOrDefault(item.Id);

    private async Task InitializeContentResourcesAsync()
    {
        // Embedded resources cannot change during a run. Validate once, not on each menu/care/render tick.
        var assets = AnimationAssets.Load();
        foreach (var item in ContentCatalog.Definitions)
        {
            bool available = false;
            try
            {
                if (item.Type == ContentType.Outfit)
                    available = (await _framePlayer.GetOutfitAvailabilityAsync(item.OutfitId!)).IsAvailable;
                else if (item.Type == ContentType.Action)
                {
                    var action = assets.Actions.SingleOrDefault(x => x.Id == item.AnimationId);
                    if (action is not null)
                    {
                        await Task.Run(() =>
                        {
                            for (int i = 0; i < action.FrameCount; i++)
                                OutfitBitmap.Validate(PackAnimationResourceProvider.Instance, $"{action.Directory}/frame-{i:0000}.png");
                        });
                        available = true;
                    }
                }
                else
                {
                    var selection = _workStage.Catalog.DefaultSelection;
                    selection = item.Type == ContentType.Desk ? selection with { DeskId = item.ScenePropId! }
                        : selection with { ComputerId = item.ScenePropId! };
                    if (_workStage.Catalog.TryResolve(selection, out var scene, out _))
                    {
                        string[] paths = item.Type == ContentType.Desk ? [scene!.Desk.Back, scene.Desk.Front] : [scene!.Computer.Image];
                        await Task.Run(() => { foreach (var path in paths) OutfitBitmap.Validate(PackAnimationResourceProvider.Instance, path); });
                        available = true;
                    }
                }
            }
            catch (Exception ex) when (OutfitCatalog.IsResourceError(ex)) { available = false; }
            _contentResources[item.Id] = available;
        }
    }

    internal async Task ApplyEquippedOutfitAsync()
    {
        string selected = CareState.Content.Equipped.GetValueOrDefault(ContentSlot.Outfit, ContentCatalog.DefaultOutfitId);
        if (selected == _framePlayer.CurrentOutfitId) return;
        _outfitApplying = true;
        bool suspended = _behavior.IsAutomaticSuspended;
        _behavior.SuspendAutomatic(true);
        try
        {
            // A yawn could have started while the replacement desk decoded. Finish it before changing the complete outfit.
            while (_behavior.CurrentSample.Kind != ClipKind.Idle || _behavior.PendingCount != 0)
            {
                if (_isClosing || _behavior.IsPaused) throw new InvalidOperationException("The outfit is waiting for a standing boundary.");
                await Task.Delay(16);
            }
            if (_isClosing) return;
            var result = !_assetsReady ? await _framePlayer.RestoreOutfitAsync(selected) : await _framePlayer.SelectOutfitAsync(selected);
            if (result.Warning is not null) CareStatus = result.Warning;
            if (!result.Success) throw new InvalidDataException(result.Warning ?? "The outfit was not applied.");
            _emotionLoadFailed = false;
        }
        finally { _outfitApplying = false; _behavior.SuspendAutomatic(suspended); }
    }

    internal async Task PlayOwnedActionAsync(string id)
    {
        var playable = ContentOwnershipService.CanPlayAction(CareState.Content, id, IsContentResourceAvailable);
        if (!playable.CanPlay || InteractionsUnavailable || IsContentInteractionBusy)
        { Say(playable.CanPlay ? "当前动作还没结束，稍等一下再表演。" : playable.Message); return; }
        var item = ContentCatalog.Get(id);
        var action = AnimationAssets.Load().Actions.Single(x => x.Id == item.AnimationId);
        var kind = Enum.Parse<ClipKind>(action.Clip);
        _preparingOwnedAction = true;
        bool suspended = _behavior.IsAutomaticSuspended;
        _behavior.SuspendAutomatic(true);
        _behavior.CancelAutomaticEmotions();
        try
        {
            await _framePlayer.WarmClipAsync(kind);
            while (_behavior.CurrentSample.Kind != ClipKind.Idle || _behavior.PendingCount != 0)
            {
                if (_isClosing || _behavior.IsPaused) return;
                await Task.Delay(16);
            }
            if (_isClosing || _behavior.IsPaused) return;
            long before = _behavior.CurrentSample.Sequence;
            if (_behavior.RequestAction(kind) != PetBehaviorRequestResult.Queued) return;
            while (_behavior.CurrentSample.Sequence <= before || _behavior.CurrentSample.Kind != ClipKind.Idle)
            {
                if (_isClosing || _behavior.IsPaused) return;
                await Task.Delay(16);
            }
            // Playing an owned animation grants no extra care, experience or currency.
        }
        catch (Exception ex) when (OutfitCatalog.IsResourceError(ex)) { if (!_isClosing) Say("这段动作暂时没加载好，收藏和鹰币不会变化。 "); }
        finally { _preparingOwnedAction = false; _behavior.SuspendAutomatic(suspended); }
    }
}
