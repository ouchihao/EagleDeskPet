using System.Windows;
using System.Windows.Threading;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    private static readonly ClipKind[] GameClips =
    [ClipKind.RpsRock, ClipKind.RpsPaper, ClipKind.RpsScissors, ClipKind.RpsWin, ClipKind.RpsLose, ClipKind.Shy];
    private readonly TimeProvider _gameTime = TimeProvider.System;
    private RpsWindow? _gameWindow;
    private Task _gameOpening = Task.CompletedTask;
    private Task _gamePausing = Task.CompletedTask;
    private Task _gameExiting = Task.CompletedTask;
    private TaskCompletionSource? _gameWindowClosed;
    private bool _gameOwnsAutomatic, _gamePreviousAutomatic, _gamePauseRequested, _gameExitRequested;
    private bool _gameThrown, _gameReactionComplete, _gameRewardConsumed, _gamePresentationFailed;
    private Guid _gameRound;

    // Includes preparation/teardown and a paused, still-open game window. No other
    // interaction may take the pet merely because one round is between clips.
    internal bool IsGameActive => _gameOwnsAutomatic || _gameWindow is not null || !_gameOpening.IsCompleted || !_gameExiting.IsCompleted;

    private async void GameMenu_OnClick(object sender, RoutedEventArgs e) => await OpenGameAsync();

    internal Task OpenGameAsync()
    {
        if (_gameWindow is not null) { _gameWindow.Activate(); return Task.CompletedTask; }
        if (!_gameOpening.IsCompleted) return _gameOpening;
        if (_isClosing || _gameExitRequested || InteractionsUnavailable || IsContentInteractionBusy || IsContentEquipmentApplying)
        {
            Say("先等当前工作、暂停或互动结束，再来猜拳吧。 ");
            return Task.CompletedTask;
        }
        _gamePauseRequested = false;
        AcquireGameAnimation();
        return _gameOpening = OpenGameCoreAsync();
    }

    private async Task OpenGameCoreAsync()
    {
        try
        {
            // Decode before the round clock starts; no missing-art idle stand-in.
            await _framePlayer.WarmClipsAsync(GameClips);
            if (GameClips.Any(kind => !_framePlayer.IsClipReady(kind)))
                throw new InvalidOperationException("Guessing-game animation is unavailable.");
            await WaitForGameIdleAsync(CancellationToken.None);
            if (_gamePauseRequested || _gameExitRequested || _isClosing) return;
            var window = new RpsWindow(PerformGameAsync, () => CareState.LastGameRewardUtc, CommitGameReward,
                new RockPaperScissorsGame(timeProvider: _gameTime, lastGameRewardUtc: CareState.LastGameRewardUtc))
            { Owner = this, Topmost = Topmost };
            _gameWindow = window;
            _gameWindowClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += GameWindow_OnClosed;
            window.Show();
            PlaceCompanion(window, above: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _gamePresentationFailed = true;
            CareStatus = "猜拳动作暂时没准备好，本次没有开始或发放奖励。";
            Say(CareStatus);
        }
        finally
        {
            if (_gameWindow is null)
            {
                await WaitForGameIdleAsync(CancellationToken.None);
                ReleaseGameAnimation();
            }
        }
    }

    private void AcquireGameAnimation()
    {
        if (_gameOwnsAutomatic) return;
        _gamePreviousAutomatic = _behavior.IsAutomaticSuspended;
        _gameOwnsAutomatic = true;
        _behavior.SuspendAutomatic(true);
        _behavior.CancelAutomaticEmotions(); // Hungry props leave through their authored exit.
    }

    private void ReleaseGameAnimation()
    {
        if (!_gameOwnsAutomatic) return;
        if (!IsGameAnimationIdle) throw new InvalidOperationException("Cannot release an unfinished game animation.");
        _behavior.SuspendAutomatic(_gamePreviousAutomatic);
        _gameOwnsAutomatic = false;
    }

    private bool IsGameAnimationIdle => _behavior.CurrentSample.Kind == ClipKind.Idle && _behavior.PendingCount == 0 &&
        !_behavior.IsWorkSceneActive && !_behavior.IsWorkRequested && !_behavior.IsHungrySceneActive && !_behavior.IsHungryRequested;

    private async Task WaitForGameIdleAsync(CancellationToken cancellationToken)
    {
        while (!IsGameAnimationIdle)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_behavior.IsPaused || _isClosing)
                throw new InvalidOperationException("The pet stopped before its game animation could finish.");
            await Task.Delay(16, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task PerformGameAsync(RpsPerformance performance, CancellationToken cancellationToken)
    {
        if (performance.Cue == RpsCue.ReturnToIdle)
        {
            // Cancellation forfeits a round, never cuts its queued or active clip.
            await WaitForGameIdleAsync(CancellationToken.None);
            return;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_gameOwnsAutomatic || _gamePauseRequested || _gameExitRequested || _isClosing || _behavior.IsPaused)
                throw new OperationCanceledException(cancellationToken);
            if (performance.Cue == RpsCue.Prepare)
            {
                if (performance.RoundId == Guid.Empty || performance.RoundId == _gameRound || performance.PetChoice.HasValue || performance.Outcome.HasValue)
                    throw new InvalidOperationException("Invalid game preparation.");
                _gameRound = performance.RoundId;
                _gameThrown = _gameReactionComplete = _gameRewardConsumed = _gamePresentationFailed = false;
                // The game enforces its three-second preparation. Stay neutral;
                // revealing a hand here would leak the precommitted choice.
                await WaitForGameIdleAsync(cancellationToken);
                return;
            }
            if (performance.RoundId != _gameRound || _gamePresentationFailed)
                throw new InvalidOperationException("Stale game presentation.");
            ClipKind clip;
            if (performance.Cue == RpsCue.Throw && !_gameThrown && !performance.Outcome.HasValue)
            {
                clip = performance.PetChoice switch
                {
                    RpsChoice.Rock => ClipKind.RpsRock,
                    RpsChoice.Paper => ClipKind.RpsPaper,
                    RpsChoice.Scissors => ClipKind.RpsScissors,
                    _ => throw new InvalidOperationException("Missing game choice."),
                };
            }
            else if (performance.Cue == RpsCue.React && _gameThrown && !_gameReactionComplete)
            {
                clip = performance.Outcome switch
                {
                    RpsOutcome.PetWin => ClipKind.RpsWin,
                    RpsOutcome.PlayerWin => ClipKind.RpsLose,
                    RpsOutcome.Draw => ClipKind.Shy,
                    _ => throw new InvalidOperationException("Missing game outcome."),
                };
            }
            else throw new InvalidOperationException("Out-of-order game presentation.");
            await PlayGameClipAsync(clip, cancellationToken);
            if (performance.Cue == RpsCue.Throw) _gameThrown = true;
            else _gameReactionComplete = true;
        }
        catch
        {
            _gamePresentationFailed = true;
            _gameReactionComplete = false;
            throw;
        }
    }

    private async Task PlayGameClipAsync(ClipKind kind, CancellationToken cancellationToken)
    {
        await WaitForGameIdleAsync(cancellationToken);
        if (!_framePlayer.IsClipReady(kind)) throw new InvalidOperationException("Game frames were not preloaded.");
        long previousSequence = _behavior.CurrentSample.Sequence;
        if (_behavior.RequestAction(kind) != PetBehaviorRequestResult.Queued)
            throw new InvalidOperationException("Game animation request was not accepted.");
        long startedSequence;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = _behavior.CurrentSample;
            if (sample.Kind == kind && sample.Sequence > previousSequence) { startedSequence = sample.Sequence; break; }
            if (_behavior.IsPaused || _isClosing || (sample.Sequence > previousSequence && sample.Kind != kind))
                throw new InvalidOperationException("Game animation was displaced before it started.");
            // Idle immediately after enqueue is NOT completion: the scheduler
            // deliberately holds an idle beat before starting this new sequence.
            await Task.Delay(16, cancellationToken);
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = _behavior.CurrentSample;
            if (IsGameAnimationIdle && sample.Sequence == startedSequence) return;
            if (_behavior.IsPaused || _isClosing || sample.Sequence != startedSequence ||
                (sample.Kind != kind && sample.Kind != ClipKind.Idle))
                throw new InvalidOperationException("Game animation was displaced before its complete ending.");
            await Task.Delay(16, cancellationToken);
        }
    }

    private bool CommitGameReward(RpsRewardClaim claim)
    {
        if (_gameRewardConsumed || !_gameOwnsAutomatic || _gamePauseRequested || _gameExitRequested || _isClosing ||
            _behavior.IsPaused || _gamePresentationFailed || !_gameReactionComplete || !IsGameAnimationIdle ||
            claim.RoundId != _gameRound || claim.MoodDelta != RockPaperScissorsGame.MoodReward) return false;
        _gameRewardConsumed = true;
        var now = _gameTime.GetUtcNow().ToUniversalTime();
        _care.Advance(now);
        // Both timestamps are high-water cursors: a backwards clock cannot turn
        // a completed animation into a second reward, even across a restart.
        if (now < CareState.LastUpdatedUtc || claim.EarnedAtUtc > now || now - claim.EarnedAtUtc > TimeSpan.FromMinutes(1) ||
            (CareState.LastGameRewardUtc is { } previous && now - previous < RockPaperScissorsGame.RewardCooldown)) return false;
        var candidate = CareState.CreateSnapshot();
        candidate.Mood = Math.Clamp(candidate.Mood + claim.MoodDelta, 0, 100);
        candidate.LastGameRewardUtc = now;
        try
        {
            if (!_store.Save(candidate)) { CareStatus = _store.Warning ?? "猜拳奖励未保存，本次没有额外增加心情。"; return false; }
            CareState.ApplySnapshot(candidate);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CareStatus = "猜拳奖励未保存，本次没有额外增加心情。";
            return false;
        }
    }

    internal Task CancelGameForPauseAsync()
    {
        _gamePauseRequested = true;
        _gameRewardConsumed = true;
        return !_gamePausing.IsCompleted ? _gamePausing : _gamePausing = PauseGameCoreAsync();
    }

    private async Task PauseGameCoreAsync()
    {
        await _gameOpening;
        if (_gameWindow is { } window) await window.CancelForPauseAsync();
        if (!_gameOwnsAutomatic) return;
        await WaitForGameIdleAsync(CancellationToken.None);
        ReleaseGameAnimation();
    }

    internal void ResumeGameAfterPause()
    {
        _gamePauseRequested = false;
        if (_gameWindow is null || _gameExitRequested || _isClosing) return;
        AcquireGameAnimation();
        _gameWindow.ResumeAfterPause();
    }

    private async void GameWindow_OnClosed(object? sender, EventArgs e)
    {
        if (sender is RpsWindow window) window.Closed -= GameWindow_OnClosed;
        try
        {
            await WaitForGameIdleAsync(CancellationToken.None);
            _gameWindow = null;
            ReleaseGameAnimation();
            _gameWindowClosed?.TrySetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Retain ownership on an abnormal renderer stop; never reset a clip
            // or tell the owner it is safe to exit when cleanup did not finish.
            CareStatus = "猜拳动作还没有收好，请恢复动作后再退出。";
            _gameWindowClosed?.TrySetException(ex);
        }
    }

    internal bool TryCloseGameForPetExit()
    {
        if (!IsGameActive) return true;
        _gameExitRequested = true;
        _gameRewardConsumed = true;
        if (_gameExiting.IsCompleted) _gameExiting = CloseGameForPetExitAsync();
        return false;
    }

    private async Task CloseGameForPetExitAsync()
    {
        try
        {
            await _gameOpening;
            if (_gameWindow is { } window)
            {
                Task closed = _gameWindowClosed!.Task;
                window.Close();
                await closed;
            }
            await WaitForGameIdleAsync(CancellationToken.None);
            ReleaseGameAnimation();
            // Runs after this task completes, so the next Closing sees no owner.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _gameExitRequested = false;
            CareStatus = "猜拳还在收尾，暂时没有退出，也没有发放奖励。";
            Say(CareStatus);
        }
    }
}
