using System.IO;
using System.Windows;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    private readonly EmotionPolicy _emotions = new();
    private bool _preparingReaction, _preparingEmotionAssets, _emotionLoadFailed, _pendingFeed;
    private HungryScenePreviewWindow? _hungryScenePreview;
    private static readonly ClipKind[] EmotionClips = [ClipKind.HungryEnter, ClipKind.HungryLoop, ClipKind.HungryExit, ClipKind.Annoyed];

    private async Task PrepareEmotionAssetsAsync()
    {
        if (_preparingEmotionAssets || _emotionLoadFailed) return;
        _preparingEmotionAssets = true;
        try { await _framePlayer.WarmClipsAsync(EmotionClips); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            _emotionLoadFailed = true;
            if (!_isClosing) Say("情绪动作暂时没有加载好，普通互动仍然可以用。");
        }
        finally { _preparingEmotionAssets = false; }
    }

    private void TickEmotions(DateTimeOffset now)
    {
        bool ready = EmotionClips.All(_framePlayer.IsClipReady);
        if (_emotions.Enabled && !ready && _assetsReady && !IsGameActive && !_emotionLoadFailed)
            _ = PrepareEmotionAssetsAsync();
        var evaluation = _emotions.Evaluate(CareState, now,
            ready && !InteractionsUnavailable && !_nativeDragInProgress && !_pendingFeed &&
            _behavior.CurrentSample.Kind == ClipKind.Idle && _behavior.PendingCount == 0 && !IsContentInteractionBusy);
        if (evaluation.CancelHungryScene) _behavior.CancelHungryScene();
        if (evaluation.Reaction != EmotionReaction.None) _behavior.QueueAutomaticEmotion(evaluation.Reaction);
        // Feeding is consumed once, only after the scene relinquishes its bowl and desk.
        if (_pendingFeed && !InteractionsUnavailable && !_behavior.IsHungrySceneActive &&
            !_behavior.IsHungryRequested && _behavior.CurrentSample.Kind == ClipKind.Idle) FeedPet();
    }

    private async Task PlayTouchComplaintAsync()
    {
        _preparingReaction = true;
        try
        {
            await _framePlayer.WarmClipAsync(ClipKind.Annoyed);
            if (!_isClosing && !_behavior.IsPaused && !WorkInProgress && !IsGameActive)
                _behavior.QueueReaction(PetBehaviorKind.Annoyed);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or NotSupportedException)
        { if (!_isClosing) CareStatus = "护头动作未准备好，这次只口头抗议。"; }
        finally { _preparingReaction = false; }
    }

    private void EmotionMenu_OnClick(object sender, RoutedEventArgs e)
    {
        _companion.AutoEmotionScenesEnabled = _emotions.Enabled = EmotionMenuItem.IsChecked;
        if (!_companion.Save()) Say("情绪开关本次已生效，但没能保存。 ");
        if (!_emotions.Enabled) _behavior.CancelAutomaticEmotions();
        else { _emotionLoadFailed = false; _ = PrepareEmotionAssetsAsync(); }
    }

    private async void HungryPreviewMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (_hungryScenePreview is not null) { _hungryScenePreview.Activate(); return; }
        var window = new HungryScenePreviewWindow(_framePlayer.CurrentOutfitId, _workStage.ActiveSelection)
            { Owner = this, Topmost = Topmost };
        _hungryScenePreview = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_hungryScenePreview, window)) _hungryScenePreview = null; };
        window.Show();
        PlaceCompanion(window, above: false);
        try { await window.PrepareAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_isClosing) CareStatus = "独立小剧场的素材未准备好；宠物状态没有改变。"; }
    }
}
