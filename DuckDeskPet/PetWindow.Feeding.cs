using System.Windows.Threading;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    private bool _feeding, _feedingCloseRequested;
    private readonly TimeProvider _feedingTime = TimeProvider.System;
    private long _feedingBeforeSequence;
    private long? _feedingSequence;
    internal bool IsFeeding => _feeding;

    internal void FeedPet()
    {
        if (InteractionsUnavailable)
        {
            Say(IsFeeding ? "这一碗还没吃完，先让我嚼完再续。" : WorkInProgress ? "先点取消工作，收工再开饭。"
                : _assetsReady ? "先恢复动作，再开饭吧。" : "饭搭子正在热身，稍等一下。 ");
            return;
        }
        if (_behavior.IsHungrySceneActive || _behavior.IsHungryRequested)
        {
            _pendingFeed = true;
            _behavior.CancelAutomaticEmotions();
            Say("收到！先把空碗和桌子收好，马上开饭。");
            return;
        }
        _pendingFeed = false;
        // Check before consuming. In particular, coalescing an existing Eat is
        // not permission to charge a second meal for that same animation.
        if (_behavior.PreviewReaction(PetBehaviorKind.Fed) != PetBehaviorRequestResult.Queued)
        {
            Say("当前动作还排着队，等这一轮结束再开饭；这次没有扣粮。 ");
            return;
        }

        _feeding = true;
        _feedingBeforeSequence = _behavior.CurrentSample.Sequence;
        _feedingSequence = null;
        bool queued = false;
        try
        {
            var now = _feedingTime.GetUtcNow();
            _care.Advance(now);
            var before = CareState.CreateSnapshot();
            var result = _care.Feed(now);
            if (result.Success)
            {
                // This admission/consume/enqueue section never awaits or pumps
                // dispatcher messages. A defensive rejection restores the care
                // snapshot before any save; it cannot charge an unseen meal.
                queued = _behavior.QueueReaction(PetBehaviorKind.Fed) == PetBehaviorRequestResult.Queued;
                if (!queued)
                {
                    CareState.ApplySnapshot(before);
                    CareStatus = "动作没有排进去，这次没有扣粮。";
                    Say(CareStatus);
                    return;
                }
                _store.Save(CareState);
            }
            CareStatus = _store.Warning ?? result.Message.Trim();
            _carePanel?.Refresh();
            Say(CareStatus);
        }
        finally { if (!queued) _feeding = false; }
    }

    // Called only after the real renderer has applied this sample. An idle
    // queue beat is not completion, and earlier queued reactions may play first.
    private void TickFeeding()
    {
        if (!_feeding) return;
        var sample = _behavior.CurrentSample;
        if (_feedingSequence is null)
        {
            if (sample.Kind == ClipKind.Eat && sample.Sequence > _feedingBeforeSequence)
                _feedingSequence = sample.Sequence;
            return;
        }
        if (sample.Kind != ClipKind.Idle || sample.Sequence != _feedingSequence.Value) return;
        _feeding = false;
        _feedingSequence = null;
        _carePanel?.Refresh();
        if (_feedingCloseRequested)
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
    }

    private bool TryCloseFeedingForPetExit()
    {
        if (!_feeding) return true;
        _feedingCloseRequested = true;
        CareStatus = "这一口先嚼完，马上收好退出。";
        return false;
    }

    private void StopFeeding()
    {
        _feeding = _feedingCloseRequested = false;
        _feedingSequence = null;
        _pendingFeed = false;
    }
}
