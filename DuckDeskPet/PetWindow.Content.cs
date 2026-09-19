using System.IO;
using System.Windows;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    private ShopTransactions? _contentTransactions;
    private ShopWindow? _shopWindow;
    private ContentPreviewWindow? _contentPreviewWindow;
    private readonly SemaphoreSlim _contentEquipmentGate = new(1, 1);
    private bool _contentStopped, _contentApplying, _contentPendingScheduled;
    private long _contentEquipmentRevision;
    private Task _contentOperation = Task.CompletedTask;
    private DateTimeOffset _nextContentRetry;
    internal bool IsContentEquipmentApplying => _contentApplying;
    internal Task PendingContentOperation => _contentOperation;

    // The owner awaits this after InitializeCare and before starting the restored work scene.
    internal async Task InitializeContent()
    {
        _contentStopped = false;
        _contentTransactions = new(_store, () => CareState, IsContentResourceAvailable, IsContentSetCompatible);
        await _contentEquipmentGate.WaitAsync();
        _contentApplying = true;
        try { await ApplySavedContentVisualsAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { CareStatus = "收藏选择已保留；部分外观暂未加载，先使用默认或原来的外观。"; }
        finally { _contentApplying = false; _contentEquipmentGate.Release(); }
    }

    internal void RefreshContent()
    {
        if (_contentStopped || _isClosing || _contentTransactions is null) return;
        _shopWindow?.Refresh();
        _honorWall?.Refresh();
    }

    // Called after the renderer applies a frame. The fast path does no resource probes or I/O.
    internal void RefreshContentAtSafeBoundary()
    {
        if (_contentStopped || _isClosing || _contentTransactions is null || _contentApplying || _contentPendingScheduled ||
            CareState.Content.PendingEquipment.Count == 0 || !ContentBoundaryIsSafe || DateTimeOffset.UtcNow < _nextContentRetry) return;
        _contentPendingScheduled = true;
        try
        {
            _contentOperation = ApplyPendingContentAsync();
        }
        catch { _contentPendingScheduled = false; throw; }
    }

    internal void StopContent()
    {
        _contentStopped = true;
        ++_contentEquipmentRevision;
        _shopWindow?.Close();
        _contentPreviewWindow?.Close();
    }

    private bool ContentBoundaryIsSafe => !_isClosing && !_contentStopped && !WorkInProgress &&
        !_nativeDragInProgress && !IsContentInteractionBusy && _behavior.CurrentSample.Kind == ClipKind.Idle;

    internal void OpenShopWindow(bool ownedOnly = false)
    {
        if (_contentStopped || _isClosing || _contentTransactions is null) return;
        if (_shopWindow is null)
        {
            var window = new ShopWindow(_contentTransactions, EquipContentAsync, PreviewContentAsync,
                PlayOwnedActionAsync, () => InteractionsUnavailable || IsContentInteractionBusy || _contentApplying, ownedOnly)
                { Owner = this, Topmost = Topmost };
            _shopWindow = window;
            window.Closed += (_, _) => { if (ReferenceEquals(_shopWindow, window)) _shopWindow = null; };
            window.Show(); PlaceCompanion(window, above: false);
        }
        else { _shopWindow.SelectOwned(ownedOnly); _shopWindow.Show(); _shopWindow.Activate(); }
    }
    private void ShopMenu_OnClick(object sender, RoutedEventArgs e) => OpenShopWindow();

    internal string ClaimHonorContent(string contentId)
    {
        if (_contentTransactions is null || _contentStopped) return "收藏还没有准备好。";
        var result = _contentTransactions.ClaimReward(contentId);
        RefreshContent();
        return result.Message;
    }

    internal async Task<ShopActionResult> EquipContentAsync(string contentId)
    {
        if (_contentTransactions is null || _contentStopped) return new(false, false, "收藏还没有准备好。");
        long revision = ++_contentEquipmentRevision;
        await _contentEquipmentGate.WaitAsync();
        try
        {
            if (_contentStopped || _isClosing) return new(false, false, "窗口正在退出，尚未更改装备。");
            if (revision != _contentEquipmentRevision) return new(false, false, "已采用更新的装备选择。");
            bool safe = ContentBoundaryIsSafe;
            _contentApplying = true;
            var result = _contentTransactions.Equip(contentId, defer: !safe);
            if (!result.Changed || !safe) return result;
            try { await ApplySavedContentVisualsAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { return new(true, true, "装备选择已经保存；外观暂未加载，所有权和选择不会丢失。"); }
            return result;
        }
        finally
        {
            _contentApplying = false;
            _contentEquipmentGate.Release();
            _shopWindow?.Refresh();
            _honorWall?.Refresh();
        }
    }

    private async Task ApplyPendingContentAsync()
    {
        await _contentEquipmentGate.WaitAsync();
        try
        {
            if (!ContentBoundaryIsSafe || _contentTransactions is null) return;
            _contentApplying = true;
            var result = _contentTransactions.ApplyPending();
            if (!result.Success) { CareStatus = result.Message; _nextContentRetry = DateTimeOffset.UtcNow.AddSeconds(15); return; }
            if (result.Changed) await ApplySavedContentVisualsAsync();
            else if (CareState.Content.PendingEquipment.Count > 0) _nextContentRetry = DateTimeOffset.UtcNow.AddSeconds(15);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { CareStatus = "外观暂未加载，装备选择和收藏均已保留。"; _nextContentRetry = DateTimeOffset.UtcNow.AddSeconds(15); }
        finally
        {
            _contentApplying = false;
            _contentPendingScheduled = false;
            _contentEquipmentGate.Release();
            _shopWindow?.Refresh();
        }
    }

    private bool IsContentSetCompatible(ContentOwnershipState content)
    {
        var future = content.CreateSnapshot();
        foreach (var pending in future.PendingEquipment) future.Equipped[pending.Key] = pending.Value;
        return TryContentSceneSelection(future, out _, out _);
    }

    private bool TryContentSceneSelection(ContentOwnershipState content, out SceneSelection selection, out string? warning)
    {
        var desk = ContentOwnershipService.ResolveEquipped(content, ContentSlot.Desk, IsContentResourceAvailable);
        var computer = ContentOwnershipService.ResolveEquipped(content, ContentSlot.Computer, IsContentResourceAvailable);
        warning = desk.Warning ?? computer.Warning;
        selection = _workStage.Catalog.DefaultSelection;
        if (desk.EffectiveId is null || computer.EffectiveId is null) return false;
        selection = selection with
        {
            DeskId = ContentCatalog.Get(desk.EffectiveId).ScenePropId!,
            ComputerId = ContentCatalog.Get(computer.EffectiveId).ScenePropId!,
        };
        return _workStage.Catalog.TryResolve(selection, out _, out _);
    }

    private async Task ApplySavedContentVisualsAsync()
    {
        if (_contentStopped || _isClosing) return;
        if (!TryContentSceneSelection(CareState.Content, out var selection, out var warning))
            throw new InvalidDataException("No compatible available workplace selection.");
        await _workStage.RequestSelectionAsync(selection);
        if (_contentStopped || _isClosing) return;
        await ApplyEquippedOutfitAsync();
        if (warning is not null) CareStatus = warning;
    }

    internal async Task PreviewContentAsync(string contentId)
    {
        if (_contentStopped || _isClosing || !ContentCatalog.TryGet(contentId, out var item)) return;
        if (!IsContentResourceAvailable(item!)) throw new InvalidDataException("Preview assets are not available.");
        _contentPreviewWindow?.Close();
        TryContentSceneSelection(CareState.Content, out var selection, out _);
        var window = new ContentPreviewWindow(item!, selection, IsContentResourceAvailable)
            { Owner = this, Topmost = Topmost };
        _contentPreviewWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_contentPreviewWindow, window)) _contentPreviewWindow = null; };
        window.Show(); PlaceCompanion(window, above: false);
        await window.PrepareAsync();
    }

    internal async Task PlayTeaAsync()
    {
        var playable = ContentOwnershipService.CanPlayAction(CareState.Content, ContentCatalog.TeaActionId, IsContentResourceAvailable);
        if (!playable.CanPlay) { Say(playable.Message); return; }
        if (InteractionsUnavailable || IsContentInteractionBusy || _contentApplying) { Say("先等当前工作或互动完整结束，再喝口茶。 "); return; }
        await PlayOwnedActionAsync(ContentCatalog.TeaActionId);
    }
}
