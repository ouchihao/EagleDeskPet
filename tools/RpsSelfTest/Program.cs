using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DuckDeskPet;
using DuckDeskPet.Core;

internal static class Program
{
    private static string? _output;

    [STAThread]
    private static int Main(string[] args)
    {
        _output = args.Length > 0 ? Path.GetFullPath(args[0]) : null;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
        var cases = new (string Name, Action Body)[]
        {
            ("All nine choice combinations resolve correctly", NineCombinations),
            ("Pet choice is committed once before player input and not disclosed early", Precommit),
            ("Duplicate clicks, starts and presentation completions cannot change a round", DuplicateInput),
            ("Invalid randomness and enum input fail without starting a round", InvalidInput),
            ("Responsive 4.6-second minimum keeps complete throw and reaction gates", FullPacing),
            ("Exclusive game starts immediately without bypassing ordinary or scene ownership", ExclusiveStarts),
            ("Clock alone cannot reveal a result before the throw completes", PresentationGate),
            ("Stale round and phase callbacks are ignored", StaleCallbacks),
            ("Only one mood claim is possible after a completed round", SingleSettlement),
            ("Every cancellation phase forfeits the reward", Cancellation),
            ("Close, pause and presentation errors never settle an unfinished round", CancellationReasons),
            ("A stalled or suspended performance times out without reward", Timeout),
            ("Player thinking time does not consume performance time", ThinkingTime),
            ("Repeat rounds within five minutes have no additional mood claim", Cooldown),
            ("Persisted cooldown survives a new game instance", RestartCooldown),
            ("Updated durable reward cursor prevents a stale window awarding again", UpdatedCursor),
            ("Backward UTC blocks rewards without altering monotonic animation pacing", ClockBackwards),
            ("Failed host commits are not retried by a round", FailedCommit),
            ("WPF buttons lock immediately and result follows performance", UiRound),
            ("WPF cancellation waits for return-to-idle and does not reward", UiPause),
            ("WPF presentation failure safely ends a round", UiFailure),
            ("WPF close before player input invokes no reward", UiClose),
            ("WPF asynchronous close waits for a safe return before releasing the window", UiAsyncClose),
            ("WPF does not reveal while an actual throw delegate is still pending", UiAsyncThrow),
        };
        int failures = 0;
        try
        {
            foreach (var (name, body) in cases)
            {
                try { body(); DrainDispatcher(); Console.WriteLine("PASS  " + name); }
                catch (Exception exception) { failures++; Console.Error.WriteLine("FAIL  " + name + "\n" + exception); }
            }
        }
        finally { application.Shutdown(); }
        Console.WriteLine($"RPS core and WPF: {cases.Length - failures}/{cases.Length} passed; no user state, real animation or account changed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private static void NineCombinations()
    {
        var expected = new[,]
        {
            { RpsOutcome.Draw, RpsOutcome.PetWin, RpsOutcome.PlayerWin },
            { RpsOutcome.PlayerWin, RpsOutcome.Draw, RpsOutcome.PetWin },
            { RpsOutcome.PetWin, RpsOutcome.PlayerWin, RpsOutcome.Draw },
        };
        foreach (RpsChoice player in Enum.GetValues<RpsChoice>())
            foreach (RpsChoice pet in Enum.GetValues<RpsChoice>())
                Check(RockPaperScissorsGame.Resolve(player, pet) == expected[(int)player, (int)pet], "Wrong outcome.");
    }
    private static void Precommit()
    {
        int calls = 0, chosen = 0;
        var game = new RockPaperScissorsGame(() => { calls++; return chosen; });
        Check(game.StartRound() && calls == 1, "Pet did not precommit before input.");
        Check(game.Snapshot.PetChoice is null && game.Snapshot.Outcome is null, "Secret choice leaked to UI.");
        chosen = 2; game.Choose(RpsChoice.Paper);
        Check(calls == 1 && game.Snapshot.PetChoice is null && game.Snapshot.Outcome is null, "Choice source reread player input or result leaked.");
    }
    private static void DuplicateInput()
    {
        var clock = new FakeTime(); var game = NewGame(clock);
        Check(!game.StartRound(), "Started a second simultaneous round.");
        Check(game.Choose(RpsChoice.Rock) && !game.Choose(RpsChoice.Paper), "Second click replaced locked choice.");
        Check(game.CompletePresentation(game.RoundId, RpsCue.Prepare), "Valid preparation rejected.");
        Check(!game.CompletePresentation(game.RoundId, RpsCue.Prepare), "Duplicate completion accepted.");
        Check(game.Snapshot.PlayerChoice == RpsChoice.Rock, "Locked input changed.");
    }
    private static void InvalidInput()
    {
        var game = new RockPaperScissorsGame(() => 5);
        bool rejected = false; try { game.StartRound(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && game.Phase == RpsPhase.Idle, "Bad randomness partially started a round.");
        game = new RockPaperScissorsGame(() => 0); game.StartRound();
        rejected = false; try { game.Choose((RpsChoice)4); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected && game.Phase == RpsPhase.AwaitingChoice, "Bad player input partially submitted.");
    }
    private static void FullPacing()
    {
        var clock = new FakeTime(); var game = NewGame(clock); game.Choose(RpsChoice.Paper);
        game.CompletePresentation(game.RoundId, RpsCue.Prepare);
        clock.Advance(.59); game.Advance(); Check(game.Phase == RpsPhase.Preparing, "Preparation ended too early.");
        clock.Advance(.01); game.Advance(); Check(game.Phase == RpsPhase.Throwing, "Throw did not follow preparation.");
        Check(game.Snapshot.Outcome is null && game.Snapshot.PetChoice == RpsChoice.Rock, "Throw result was exposed early.");
        Step(game, clock, RpsCue.Throw, 2);
        Check(game.Phase == RpsPhase.Reacting && game.Snapshot.Outcome == RpsOutcome.PlayerWin, "Wrong reveal.");
        game.CompletePresentation(game.RoundId, RpsCue.React); clock.Advance(1.99); game.Advance();
        Check(game.Phase == RpsPhase.Reacting, "Reaction/hold ended too early.");
        clock.Advance(.01); game.Advance();
        Check(game.Phase == RpsPhase.Completed && Math.Abs(game.Snapshot.RoundElapsedSeconds - 4.6) < 1e-8, "Minimum round was not 4.6 seconds.");
    }
    private static void ExclusiveStarts()
    {
        var behavior = new PetBehaviorController();
        Check(!behavior.TryStartExclusiveGameAction(ClipKind.RpsRock), "Unowned game bypassed automatic action ownership.");
        behavior.SuspendAutomatic(true);
        Check(behavior.TryStartExclusiveGameAction(ClipKind.RpsRock) && behavior.CurrentSample is { Kind: ClipKind.RpsRock, Progress: 0, Sequence: 1 }, "Owned idle did not synchronously return the authored first frame.");
        Check(!behavior.TryStartExclusiveGameAction(ClipKind.RpsPaper), "Game interrupted a current action.");
        while (behavior.CurrentSample.Kind != ClipKind.Idle) behavior.Advance(.05);
        Check(behavior.TryStartExclusiveGameAction(ClipKind.RpsWin) && behavior.CurrentSample.Sequence == 2, "Reaction inserted an unwanted two-second idle beat.");
        while (behavior.CurrentSample.Kind != ClipKind.Idle) behavior.Advance(.05);
        behavior.QueueReaction(PetBehaviorKind.Fed);
        Check(!behavior.TryStartExclusiveGameAction(ClipKind.Shy) && behavior.PendingCount == 1, "Game stole queued food.");
        for (int i = 0; i < 39; i++) behavior.Advance(.05);
        Check(behavior.CurrentSample.Kind == ClipKind.Idle && behavior.PendingCount == 1, "Ordinary feeding lost its two-second idle beat.");
        behavior.Advance(.05);
        Check(behavior.CurrentSample.Kind == ClipKind.Eat, "Ordinary feeding did not start at two seconds.");
        behavior = new PetBehaviorController(); behavior.SuspendAutomatic(true); behavior.SetWorkState(true, false);
        Check(!behavior.TryStartExclusiveGameAction(ClipKind.RpsRock), "Game stole pending work.");
        behavior.Advance(.01); Check(!behavior.TryStartExclusiveGameAction(ClipKind.RpsRock), "Game stole active work.");
        behavior = new PetBehaviorController(); behavior.SuspendAutomatic(true); behavior.RequestHungryScene();
        Check(!behavior.TryStartExclusiveGameAction(ClipKind.RpsRock), "Game stole pending hungry props.");
        for (int i = 0; i < 40; i++) behavior.Advance(.05);
        Check(!behavior.TryStartExclusiveGameAction(ClipKind.RpsRock), "Game stole active hungry props.");
        behavior = new PetBehaviorController(); behavior.SuspendAutomatic(true); behavior.SetPaused(true);
        Check(!behavior.TryStartExclusiveGameAction(ClipKind.RpsRock), "Game bypassed pause.");
        bool rejected = false;
        try { behavior.TryStartExclusiveGameAction(ClipKind.Eat); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "General feeding used a game-only shortcut.");
    }
    private static void PresentationGate()
    {
        var clock = new FakeTime(); var game = NewGame(clock); game.Choose(RpsChoice.Paper);
        Step(game, clock, RpsCue.Prepare, 3); clock.Advance(3); game.Advance();
        Check(game.Phase == RpsPhase.Throwing && game.Snapshot.Outcome is null, "Timer revealed before throw completion.");
        Check(!game.TryTakeReward(null, out _), "Incomplete presentation awarded mood.");
        game.CompletePresentation(game.RoundId, RpsCue.Throw); game.Advance();
        Check(game.Phase == RpsPhase.Reacting, "Completed throw could not reveal.");
    }
    private static void StaleCallbacks()
    {
        var clock = new FakeTime(); var game = NewGame(clock); Guid first = game.RoundId;
        game.Choose(RpsChoice.Paper); game.Cancel(RpsCancelReason.UserExit); game.StartRound(); game.Choose(RpsChoice.Rock);
        Check(!game.CompletePresentation(first, RpsCue.Prepare) && !game.CompletePresentation(game.RoundId, RpsCue.React), "Old callback advanced another round.");
        clock.Advance(4); game.Advance(); Check(game.Phase == RpsPhase.Preparing, "Stale callback unlocked a phase.");
    }
    private static void SingleSettlement()
    {
        var clock = new FakeTime(); var game = NewGame(clock); Complete(game, clock);
        Check(game.TryTakeReward(null, out var reward) && reward is { MoodDelta: 2 } && reward.RoundId == game.RoundId,
            "Completed round did not issue its limited mood claim.");
        Check(!game.TryTakeReward(null, out _) && !game.TryTakeReward(null, out _), "Duplicate settlement.");
    }
    private static void Cancellation()
    {
        for (int stage = 0; stage < 5; stage++)
        {
            var clock = new FakeTime(); var game = NewGame(clock);
            if (stage >= 1) game.Choose(RpsChoice.Rock);
            if (stage >= 2) Step(game, clock, RpsCue.Prepare, 3);
            if (stage >= 3) Step(game, clock, RpsCue.Throw, 2);
            if (stage >= 4) Step(game, clock, RpsCue.React, 5);
            game.Cancel(RpsCancelReason.UserExit); clock.Advance(400); game.Advance();
            Check(!game.TryTakeReward(null, out _) && game.Phase == RpsPhase.Cancelled, "Cancelled phase issued a late reward.");
        }
    }
    private static void CancellationReasons()
    {
        foreach (var reason in Enum.GetValues<RpsCancelReason>())
        {
            var clock = new FakeTime(); var game = NewGame(clock); game.Choose(RpsChoice.Rock); game.Cancel(reason);
            Check(game.Snapshot.Cancellation == reason && !game.TryTakeReward(null, out _), "Cancellation settled a reward.");
        }
    }
    private static void Timeout()
    {
        var clock = new FakeTime(); var game = NewGame(clock); game.Choose(RpsChoice.Paper);
        clock.Advance(20); game.Advance();
        Check(game.Phase == RpsPhase.Cancelled && game.Snapshot.Cancellation == RpsCancelReason.TimedOut && !game.TryTakeReward(null, out _), "Timeout did not forfeit.");
    }
    private static void ThinkingTime()
    {
        var clock = new FakeTime(); var game = NewGame(clock); clock.Advance(600); game.Advance();
        Check(game.Phase == RpsPhase.AwaitingChoice, "User was rushed into a cancelled choice.");
        Complete(game, clock); Check(game.Phase == RpsPhase.Completed, "Thinking time shortened animation budget.");
    }
    private static void Cooldown()
    {
        var clock = new FakeTime(); var game = NewGame(clock); Complete(game, clock); Check(game.TryTakeReward(null, out _), "Initial reward absent.");
        game.StartRound(); Complete(game, clock); Check(!game.TryTakeReward(null, out _), "Immediate replay farmed mood.");
        clock.Advance(300); game.StartRound(); Complete(game, clock); Check(game.TryTakeReward(null, out _), "Five-minute cooldown did not expire.");
    }
    private static void RestartCooldown()
    {
        var clock = new FakeTime(); var first = NewGame(clock); Complete(first, clock); first.TryTakeReward(null, out var saved);
        var second = new RockPaperScissorsGame(() => 0, clock, saved!.EarnedAtUtc); second.StartRound(); Complete(second, clock);
        Check(!second.TryTakeReward(saved.EarnedAtUtc, out _), "Restart bypassed the durable cursor.");
    }
    private static void UpdatedCursor()
    {
        var clock = new FakeTime(); var game = NewGame(clock); Complete(game, clock);
        Check(!game.TryTakeReward(clock.GetUtcNow().AddSeconds(-1), out _), "A stale window ignored a newer persisted reward.");
    }
    private static void ClockBackwards()
    {
        var clock = new FakeTime(); DateTimeOffset saved = clock.GetUtcNow();
        var game = new RockPaperScissorsGame(() => 0, clock, saved); game.StartRound(); clock.JumpUtc(-3600); Complete(game, clock);
        Check(game.Phase == RpsPhase.Completed && !game.TryTakeReward(saved, out _), "Clock rollback changed phases or awarded mood.");
    }
    private static void FailedCommit()
    {
        var clock = new FakeTime(); var game = NewGame(clock); Complete(game, clock);
        Check(game.TryTakeReward(null, out _), "No claim to attempt.");
        // Host rejected its atomic save. Re-entering the handler must not try again.
        Check(!game.TryTakeReward(null, out _), "Failed save permitted another claim.");
    }

    private static void UiRound()
    {
        var clock = new FakeTime(); var game = new RockPaperScissorsGame(() => 0, clock);
        var cues = new List<RpsPerformance>(); int awards = 0;
        var window = new RpsWindow((request, _) => { cues.Add(request); return Task.CompletedTask; }, () => null, claim => { awards++; return true; }, game);
        try
        {
            Load(window); Save(window, "rps-choice.png"); Click(window, "PaperButton");
            Check(window.Width <= 320 && window.Height <= 360 && window.ResizeMode == ResizeMode.NoResize, "Game is not a compact companion window.");
            Check(!Find<Button>(window, "RockButton").IsEnabled && !Find<Button>(window, "ReplayButton").IsEnabled, "Choices remained active after input.");
            clock.Advance(.6); window.Refresh();
            Check(Find<TextBlock>(window, "StageText").Text == "出拳！" && game.Snapshot.Outcome is null && awards == 0, "UI revealed or settled too soon.");
            Check(Find<TextBlock>(window, "PetHandLabel").Text == "已选好", "Panel spoiled the hand before its animation ended.");
            clock.Advance(2); window.Refresh(); Check(Find<TextBlock>(window, "StageText").Text == "你赢了！", "UI disagrees with result.");
            Check(awards == 0, "Reaction began by awarding instead of finishing the round.");
            clock.Advance(2); window.Refresh(); window.Refresh();
            Check(awards == 1 && Find<Button>(window, "ReplayButton").IsEnabled, "Completed round did not settle once and unlock replay.");
            Check(cues.Select(x => x.Cue).SequenceEqual(new[] { RpsCue.Prepare, RpsCue.Throw, RpsCue.React, RpsCue.ReturnToIdle }), "Incomplete or duplicate performance cues.");
            Check(cues[0].PetChoice is null && cues[0].Outcome is null && cues[1].Outcome is null, "Early request leaked a result.");
            Save(window, "rps-result.png");
        }
        finally { window.Close(); }
    }
    private static void UiPause()
    {
        var clock = new FakeTime(); int awards = 0; var cues = new List<RpsCue>();
        var window = new RpsWindow((request, _) => { cues.Add(request.Cue); return Task.CompletedTask; }, () => null, _ => { awards++; return true; }, new(() => 0, clock));
        try
        {
            Load(window); Click(window, "RockButton"); window.CancelForPauseAsync().GetAwaiter().GetResult();
            clock.Advance(30); window.Refresh();
            Check(awards == 0 && cues.Count(x => x == RpsCue.ReturnToIdle) == 1 && !Find<Button>(window, "ReplayButton").IsEnabled,
                "Pause rewarded, skipped safe teardown or allowed play while paused.");
            Check(!Find<Border>(window, "StageSurface").HasAnimatedProperties, "Cancelled round retained a stage animation clock.");
            window.ResumeAfterPause(); Check(Find<Button>(window, "ReplayButton").IsEnabled, "Resume did not permit a fresh round.");
        }
        finally { window.Close(); }
    }
    private static void UiFailure()
    {
        int awards = 0, returns = 0;
        var window = new RpsWindow((request, _) =>
        {
            if (request.Cue == RpsCue.ReturnToIdle) { returns++; return Task.CompletedTask; }
            return Task.FromException(new InvalidOperationException("Synthetic missing animation"));
        }, () => null, _ => { awards++; return true; });
        try
        {
            Load(window); Click(window, "RockButton"); window.Refresh();
            Check(awards == 0 && returns == 1 && Find<TextBlock>(window, "StageText").Text == "这一局先收摊", "Failed presentation did not safely cancel.");
        }
        finally { window.Close(); }
    }
    private static void UiClose()
    {
        int awards = 0; var window = new RpsWindow((_, _) => Task.CompletedTask, () => null, _ => { awards++; return true; });
        Load(window); window.Close(); Check(awards == 0, "Closing a choice screen rewarded mood.");
        Check(!Find<Border>(window, "StageSurface").HasAnimatedProperties, "Closing a choice screen retained a stage animation clock.");
    }
    private static void UiAsyncClose()
    {
        int awards = 0, returns = 0; bool closed = false;
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new RpsWindow((request, token) =>
        {
            if (request.Cue == RpsCue.ReturnToIdle) { returns++; return returning.Task; }
            token.Register(() => playing.TrySetCanceled(token));
            return playing.Task;
        }, () => null, _ => { awards++; return true; });
        window.Closed += (_, _) => closed = true;
        Load(window); Click(window, "RockButton"); window.Close();
        PumpUntil(() => returns == 1);
        Check(!closed && awards == 0, "Window closed or rewarded before props safely returned.");
        returning.SetResult(); PumpUntil(() => closed);
        Check(returns == 1 && awards == 0, "Close duplicated teardown or awarded a cancelled round.");
    }
    private static void UiAsyncThrow()
    {
        var clock = new FakeTime(); var game = new RockPaperScissorsGame(() => 0, clock);
        var throwing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new RpsWindow((request, _) => request.Cue == RpsCue.Throw ? throwing.Task : Task.CompletedTask,
            () => null, _ => true, game);
        try
        {
            Load(window); Click(window, "PaperButton"); clock.Advance(3); window.Refresh();
            clock.Advance(3); window.Refresh();
            Check(game.Phase == RpsPhase.Throwing && Find<TextBlock>(window, "StageText").Text == "出拳！", "UI revealed before animation Task completed.");
            throwing.SetResult(); PumpUntil(() => window.PendingOperation.IsCompleted); window.Refresh();
            Check(game.Phase == RpsPhase.Reacting && Find<TextBlock>(window, "StageText").Text == "你赢了！", "UI failed to reveal after actual throw completion.");
        }
        finally { window.Close(); }
    }

    private static RockPaperScissorsGame NewGame(FakeTime clock) { var game = new RockPaperScissorsGame(() => 0, clock); game.StartRound(); return game; }
    private static void Step(RockPaperScissorsGame game, FakeTime clock, RpsCue cue, double seconds)
    {
        Check(game.CompletePresentation(game.RoundId, cue), "Could not acknowledge " + cue);
        clock.Advance(seconds); game.Advance();
    }
    private static void Complete(RockPaperScissorsGame game, FakeTime clock)
    {
        game.Choose(RpsChoice.Paper);
        Step(game, clock, RpsCue.Prepare, RockPaperScissorsGame.PreparationSeconds);
        Step(game, clock, RpsCue.Throw, RockPaperScissorsGame.ThrowSeconds);
        Step(game, clock, RpsCue.React, RockPaperScissorsGame.ReactionSeconds);
    }
    private static T Find<T>(RpsWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void Load(RpsWindow window) => window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
    private static void Click(RpsWindow window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Func<bool> complete)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!complete())
        {
            DrainDispatcher();
            if (timeout.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Expected async UI completion did not arrive.");
        }
        DrainDispatcher();
    }
    private static void Save(RpsWindow window, string name)
    {
        if (_output is null) return;
        Directory.CreateDirectory(_output);
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, window.Height)); content.Arrange(new Rect(0, 0, window.Width, window.Height)); content.UpdateLayout();
        // Render steady state rather than the first, deliberately translucent transition tick.
        Find<Border>(window, "StageSurface").BeginAnimation(UIElement.OpacityProperty, null);
        var bitmap = new RenderTargetBitmap((int)window.Width * 2, (int)window.Height * 2, 192, 192, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_output, name)); encoder.Save(stream);
    }
    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(double seconds) { long ticks = (long)Math.Round(seconds * TimeSpan.TicksPerSecond); _ticks += ticks; _utc = _utc.AddTicks(ticks); }
        internal void JumpUtc(double seconds) => _utc = _utc.AddSeconds(seconds);
    }
}
