using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DuckDeskPet;
using DuckDeskPet.Core;

internal static class Program
{
    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "EaglePlayIntegration-" + Guid.NewGuid().ToString("N"));
    private static int _failed;

    [STAThread]
    private static int Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
        application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var cases = new (string Name, Func<Task> Body)[]
            {
                ("Unready, work, pause, content playback and equipment reject entry", EntryGuards),
                ("Preload and existing deliberate animation drain before opening", OpenDrain),
                ("Automatic hungry scene exits normally before game window opens", HungryExit),
                ("Decode failure starts no game and restores prior suspension", DecodeFailure),
                ("All three hands and outcomes await actual new animation sequences", Mappings),
                ("Extended gesture holds and retractions retain ownership until their final neutral samples", ExtendedClipDrain),
                ("Cancellation beyond the legacy two-second cutoff still drains every new tail frame", CancelExtendedTail),
                ("A pre-cancelled request starts no clip and cannot reward", CancelBeforeStart),
                ("Cancellation mid-animation waits through the final frame", CancelDuringClip),
                ("Pause waits for immediate throw, forfeits reward and resumes safely", PauseResume),
                ("Closing the real game window drains active throw before release", CloseGameWindow),
                ("Pet exit during preload waits and never flashes a game window", ExitWhileLoading),
                ("Pet exit during throw retries Close only after full teardown", ExitDuringThrow),
                ("A real WPF round reveals only after animation and atomically saves +2", FullUiRound),
                ("Durable five-minute cooldown and duplicate claims cannot farm mood", RewardCooldown),
                ("Future reward and backward care clocks deny reward", RewardClockRollback),
                ("CAS save failure applies neither mood nor reward cursor", RewardSaveFailure),
                ("Missing frames and stale/out-of-order callbacks cannot reward", InvalidPresentations),
            };
            foreach (var (name, body) in cases)
            {
                try { await body(); Console.WriteLine("PASS  " + name); }
                catch (Exception exception) { _failed++; Console.Error.WriteLine("FAIL  " + name + "\n" + exception); }
            }
            Console.WriteLine($"Play integration: {cases.Length - _failed}/{cases.Length} passed. Only isolated state at {Scratch}; no real saves or clients touched.");
            application.Shutdown(_failed == 0 ? 0 : 1);
        }));
        return application.Run();
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static (PetWindow Pet, TestTime Time, string Directory) NewPet()
    {
        var directory = Path.Combine(Scratch, Guid.NewGuid().ToString("N"));
        var clock = new TestTime();
        return (new PetWindow(directory, clock), clock, directory);
    }

    private static async Task Until(PetWindow pet, Func<bool> condition, bool advance = true)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(8))
                throw new TimeoutException($"Sequence did not finish: {pet.Behavior.CurrentSample}, pending {pet.Behavior.PendingCount}.");
            if (advance) pet.Behavior.Advance(.05);
            await Task.Delay(6);
        }
    }
    private static async Task Drive(PetWindow pet, Task task)
    { await Until(pet, () => task.IsCompleted); await task; }
    private static async Task Close(PetWindow pet)
    {
        if (pet.WasClosed) return;
        pet.Close();
        await Until(pet, () => pet.WasClosed);
    }
    private static async Task ExpectFailure(Task task)
    {
        bool failed = false;
        try { await task; } catch (Exception) { failed = true; }
        Check(failed, "Expected rejection did not occur.");
    }

    private static async Task<Guid> Prepare(PetWindow pet)
    {
        Guid round = Guid.NewGuid();
        await pet.Perform(new(round, RpsCue.Prepare));
        return round;
    }
    private static async Task CompletePresentation(PetWindow pet, Guid round, RpsChoice choice = RpsChoice.Rock, RpsOutcome outcome = RpsOutcome.PetWin)
    {
        await Drive(pet, pet.Perform(new(round, RpsCue.Throw, choice)));
        await Drive(pet, pet.Perform(new(round, RpsCue.React, choice, outcome)));
    }
    private static void Click(RpsWindow window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task EntryGuards()
    {
        var (pet, _, _) = NewPet();
        try
        {
            pet.AssetsReady = false; await pet.OpenGameAsync(); Check(!pet.IsGameActive, "Unready entry allowed."); pet.AssetsReady = true;
            pet.CareState.IsWorking = true; await pet.OpenGameAsync(); Check(!pet.IsGameActive, "Working entry allowed."); pet.CareState.IsWorking = false;
            pet.Behavior.SetPaused(true); await pet.OpenGameAsync(); Check(!pet.IsGameActive, "Paused entry allowed."); pet.Behavior.SetPaused(false);
            pet.ContentBusy = true; await pet.OpenGameAsync(); Check(!pet.IsGameActive, "Content playback entry allowed."); pet.ContentBusy = false;
            pet.EquipmentApplying = true; await pet.OpenGameAsync(); Check(!pet.IsGameActive, "Equipment transition entry allowed.");
        }
        finally { await Close(pet); }
    }

    private static async Task OpenDrain()
    {
        var (pet, _, _) = NewPet();
        try
        {
            pet.Behavior.SuspendAutomatic(true);
            pet.Behavior.RequestAction(ClipKind.Yawn);
            while (pet.Behavior.CurrentSample.Kind == ClipKind.Idle) pet.Behavior.Advance(.05);
            long original = pet.Behavior.CurrentSample.Sequence;
            pet.Behavior.QueueReaction(PetBehaviorKind.Petted);
            pet.Frames.WarmGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var opening = pet.OpenGameAsync();
            Check(pet.IsGameActive && pet.GameWindow is null && pet.Behavior.PendingCount == 1, "Preload failed to reserve or discarded deliberate action.");
            await Task.Delay(30);
            Check(pet.Behavior.CurrentSample.Sequence == original && !opening.IsCompleted, "Opening reset the current animation.");
            await Until(pet, () => pet.Behavior.CurrentSample.Kind == ClipKind.Idle && pet.Behavior.PendingCount == 0);
            Check(!opening.IsCompleted && pet.GameWindow is null, "Unready game window appeared before preload; old animation should drain concurrently.");
            pet.Frames.WarmGate.SetResult();
            await Drive(pet, opening);
            Check(pet.GameWindow is not null && pet.Behavior.CurrentSample.Sequence > original && pet.Behavior.PendingCount == 0, "Existing queue was not drained.");
            Check(pet.Frames.Warmed.Count == 6 && pet.OwnsAnimation, "Required clip preload or ownership missing.");
            pet.GameWindow!.Close();
            await Until(pet, () => !pet.IsGameActive);
            Check(pet.Behavior.IsAutomaticSuspended, "Preexisting automatic suspension was overwritten.");
        }
        finally { await Close(pet); }
    }

    private static async Task HungryExit()
    {
        var (pet, _, _) = NewPet();
        try
        {
            pet.Behavior.SuspendAutomatic(true); pet.Behavior.RequestHungryScene();
            while (!pet.Behavior.IsHungrySceneActive) pet.Behavior.Advance(.05);
            var opening = pet.OpenGameAsync();
            Check(!opening.IsCompleted && pet.Behavior.IsHungrySceneActive, "Hungry scene was instantly removed.");
            await Drive(pet, opening);
            Check(!pet.Behavior.IsHungrySceneActive && pet.GameWindow is not null, "Hungry exit failed to drain.");
        }
        finally { await Close(pet); }
    }

    private static async Task DecodeFailure()
    {
        var (pet, _, _) = NewPet();
        try
        {
            pet.Frames.FailWarm = true; await pet.OpenGameAsync();
            Check(!pet.IsGameActive && pet.GameWindow is null && !pet.Behavior.IsAutomaticSuspended && pet.CareState.LastGameRewardUtc is null, "Decode failure retained ownership or rewarded.");
        }
        finally { await Close(pet); }
    }

    private static async Task Mappings()
    {
        var (pet, _, _) = NewPet();
        try
        {
            await pet.OpenGameAsync();
            var hands = new[] { ClipKind.RpsRock, ClipKind.RpsPaper, ClipKind.RpsScissors };
            var reactions = new[] { ClipKind.RpsLose, ClipKind.RpsWin, ClipKind.Shy };
            for (int i = 0; i < 3; i++)
            {
                Guid round = await Prepare(pet);
                Check(pet.Behavior.CurrentSample.Kind == ClipKind.Idle, "Preparation leaked a hand.");
                var performance = pet.Perform(new(round, RpsCue.Throw, (RpsChoice)i));
                Check(!performance.IsCompleted && pet.Behavior.CurrentSample.Kind == hands[i] && pet.Behavior.PendingCount == 0 && pet.Behavior.CurrentSample.Progress == 0,
                    "Immediate throw failed to start at its authored first frame or was mistaken for completion.");
                await Until(pet, () => pet.Behavior.CurrentSample.Kind == hands[i]);
                await Task.Delay(25);
                Check(!performance.IsCompleted, "Started clip immediately acknowledged as complete.");
                await Drive(pet, performance);
                var reaction = pet.Perform(new(round, RpsCue.React, (RpsChoice)i, (RpsOutcome)i));
                await Until(pet, () => pet.Behavior.CurrentSample.Kind == reactions[i]);
                await Task.Delay(25); await Drive(pet, reaction);
                Check(pet.Behavior.CurrentSample.Kind == ClipKind.Idle && pet.OwnsAnimation, "Between-round exclusivity was released.");
            }
        }
        finally { await Close(pet); }
    }

    private static async Task ExtendedClipDrain()
    {
        var (pet, _, _) = NewPet();
        try
        {
            await pet.OpenGameAsync();
            var hands = new[] { ClipKind.RpsRock, ClipKind.RpsPaper, ClipKind.RpsScissors };
            var reactions = new[] { ClipKind.RpsLose, ClipKind.RpsWin, ClipKind.Shy };
            for (int index = 0; index < hands.Length; index++)
            {
                Guid round = await Prepare(pet);
                await Drain(hands[index], pet.Perform(new(round, RpsCue.Throw, (RpsChoice)index)), round);
                await Drain(reactions[index], pet.Perform(new(round, RpsCue.React, (RpsChoice)index, (RpsOutcome)index)), round);
            }
            async Task Drain(ClipKind kind, Task performance, Guid round)
            {
                var definition = ClipCatalog.GetDefinition(kind);
                Check(pet.Behavior.CurrentSample is { Progress: 0 } && !performance.IsCompleted, "Clip did not begin at its authored neutral boundary.");
                for (int frame = 1; frame < definition.FrameCount; frame++)
                {
                    var sample = pet.Behavior.Advance(1.0 / 60);
                    Check(sample.Kind == kind && Math.Abs(sample.FrameCoordinate - frame) < 1e-8,
                        $"{kind} skipped frame {frame} before its actual completion.");
                    if (frame is 56 or 108 or 120 || frame == definition.FrameCount - 1)
                    {
                        await Task.Delay(25);
                        Check(!performance.IsCompleted && pet.OwnsAnimation &&
                              !pet.Reward(new(round, RockPaperScissorsGame.MoodReward, DateTimeOffset.UtcNow)),
                            $"{kind} released or rewarded during the gesture hold/retraction/terminal sample.");
                    }
                }
                Check(pet.Behavior.CurrentSample.Phase == ClipPlaybackPhase.Terminal, "Final neutral was not presented as terminal.");
                pet.Behavior.Advance(1.0 / 60);
                await Until(pet, () => performance.IsCompleted, advance: false);
                await performance;
                Check(pet.Behavior.CurrentSample.Kind == ClipKind.Idle && pet.OwnsAnimation,
                    "The complete clip did not retain between-clip game ownership.");
            }
        }
        finally { await Close(pet); }
    }

    private static async Task CancelExtendedTail()
    {
        var (pet, _, _) = NewPet();
        try
        {
            await pet.OpenGameAsync();
            foreach (var kind in new[] { ClipKind.RpsRock, ClipKind.RpsPaper, ClipKind.RpsScissors, ClipKind.RpsWin, ClipKind.RpsLose })
            {
                Guid round = await Prepare(pet);
                using var cancellation = new CancellationTokenSource();
                Task performance;
                if (kind is ClipKind.RpsWin or ClipKind.RpsLose)
                {
                    await Drive(pet, pet.Perform(new(round, RpsCue.Throw, RpsChoice.Rock)));
                    performance = pet.Perform(new(round, RpsCue.React, RpsChoice.Rock,
                        kind == ClipKind.RpsWin ? RpsOutcome.PetWin : RpsOutcome.PlayerWin), cancellation.Token);
                }
                else
                {
                    var choice = kind == ClipKind.RpsRock ? RpsChoice.Rock : kind == ClipKind.RpsPaper ? RpsChoice.Paper : RpsChoice.Scissors;
                    performance = pet.Perform(new(round, RpsCue.Throw, choice), cancellation.Token);
                }
                for (int frame = 0; frame < 132; frame++) pet.Behavior.Advance(1.0 / 60);
                await Task.Delay(25);
                Check(!performance.IsCompleted && pet.Behavior.CurrentSample.Kind == kind &&
                      pet.Behavior.CurrentSample.ElapsedSeconds > 2, "The old two-second cutoff truncated an extended clip.");
                cancellation.Cancel();
                await ExpectFailure(performance);
                var cleanup = pet.Perform(new(round, RpsCue.ReturnToIdle));
                Check(!cleanup.IsCompleted && pet.OwnsAnimation, "Cancellation reset or released an unfinished extended tail.");
                for (int remaining = 0; remaining < ClipCatalog.GetDefinition(kind).FrameCount &&
                     pet.Behavior.CurrentSample.Phase != ClipPlaybackPhase.Terminal; remaining++)
                    Check(pet.Behavior.Advance(1.0 / 60).Kind == kind, "Cancellation replaced the extended tail before its terminal frame.");
                Check(pet.Behavior.CurrentSample.Phase == ClipPlaybackPhase.Terminal, "Cancelled clip never reached its authored terminal frame.");
                await Task.Delay(25);
                Check(!cleanup.IsCompleted, "Cancellation skipped the final neutral sample.");
                pet.Behavior.Advance(1.0 / 60);
                await Until(pet, () => cleanup.IsCompleted, advance: false); await cleanup;
                Check(pet.Behavior.CurrentSample.Kind == ClipKind.Idle && pet.OwnsAnimation &&
                      !pet.Reward(new(round, RockPaperScissorsGame.MoodReward, DateTimeOffset.UtcNow)),
                    "Cancelled extended performance failed to drain, leaked ownership, or rewarded.");
            }
        }
        finally { await Close(pet); }
    }

    private static async Task CancelBeforeStart()
    {
        var (pet, time, _) = NewPet();
        try
        {
            await pet.OpenGameAsync(); Guid round = await Prepare(pet);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await ExpectFailure(pet.Perform(new(round, RpsCue.Throw, RpsChoice.Rock), cancellation.Token));
            Check(pet.Behavior.CurrentSample.Kind == ClipKind.Idle && pet.Behavior.PendingCount == 0, "Already-cancelled request started an action.");
            await pet.Perform(new(round, RpsCue.ReturnToIdle));
            Check(!pet.Reward(new(round, 2, time.GetUtcNow())), "Cancelled request rewarded mood.");
        }
        finally { await Close(pet); }
    }
    private static async Task CancelDuringClip() => await CancellationScenario(beforeStart: false);
    private static async Task CancellationScenario(bool beforeStart)
    {
        var (pet, time, _) = NewPet();
        try
        {
            await pet.OpenGameAsync(); Guid round = await Prepare(pet);
            using var cancellation = new CancellationTokenSource();
            var performance = pet.Perform(new(round, RpsCue.Throw, RpsChoice.Rock), cancellation.Token);
            if (!beforeStart) { await Until(pet, () => pet.Behavior.CurrentSample.Kind == ClipKind.RpsRock); await Task.Delay(25); }
            cancellation.Cancel(); await ExpectFailure(performance);
            var cleanup = pet.Perform(new(round, RpsCue.ReturnToIdle));
            Check(!cleanup.IsCompleted && pet.OwnsAnimation, "Cancellation discarded pending/current clip.");
            await Drive(pet, cleanup);
            Check(!pet.Reward(new(round, 2, time.GetUtcNow())) && pet.CareState.LastGameRewardUtc is null, "Cancelled performance received reward.");
        }
        finally { await Close(pet); }
    }

    private static async Task BeginUiThrow(PetWindow pet, TestTime time)
    {
        await pet.OpenGameAsync();
        Click(pet.GameWindow!, "RockButton");
        time.Advance(.6); pet.GameWindow!.Refresh();
        Check(pet.Behavior.PendingCount == 0 && pet.Behavior.CurrentSample.Kind is ClipKind.RpsRock or ClipKind.RpsPaper or ClipKind.RpsScissors,
            "Real game window did not begin its throw immediately after brief anticipation.");
    }

    private static async Task PauseResume()
    {
        var (pet, time, _) = NewPet();
        try
        {
            await BeginUiThrow(pet, time);
            Task pausing = pet.CancelGameForPauseAsync();
            Check(!pausing.IsCompleted && !pet.Behavior.IsPaused, "Pause did not wait for animation.");
            await Drive(pet, pausing);
            Check(!pet.OwnsAnimation && pet.IsGameActive && !pet.Behavior.IsAutomaticSuspended && pet.CareState.LastGameRewardUtc is null, "Pause did not release at idle or awarded.");
            pet.Behavior.SetPaused(true); pet.Behavior.SetPaused(false); pet.ResumeGameAfterPause();
            Check(pet.OwnsAnimation && pet.Behavior.IsAutomaticSuspended, "Resume did not reacquire exclusivity.");
            Click(pet.GameWindow!, "ReplayButton");
            Check(((Button)pet.GameWindow!.FindName("RockButton")).IsEnabled, "Safe resume did not allow a fresh round.");
        }
        finally { await Close(pet); }
    }

    private static async Task CloseGameWindow()
    {
        var (pet, time, _) = NewPet();
        try
        {
            await BeginUiThrow(pet, time);
            await Until(pet, () => pet.Behavior.CurrentSample.Kind != ClipKind.Idle); await Task.Delay(25);
            pet.GameWindow!.Close();
            Check(pet.IsGameActive, "Closing released an active clip.");
            await Until(pet, () => !pet.IsGameActive);
            Check(pet.Behavior.CurrentSample.Kind == ClipKind.Idle && pet.CareState.LastGameRewardUtc is null, "Closing skipped final frame or awarded.");
        }
        finally { await Close(pet); }
    }

    private static async Task ExitWhileLoading()
    {
        var (pet, _, _) = NewPet();
        pet.Frames.WarmGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = pet.OpenGameAsync(); pet.Close();
        Check(!pet.WasClosed && pet.IsGameActive, "Pet exited during pending preload.");
        pet.Frames.WarmGate.SetResult(); await Drive(pet, opening);
        await Until(pet, () => pet.WasClosed);
        Check(pet.GameWindow is null && !pet.OwnsAnimation, "Game appeared or retained ownership after exit request.");
    }

    private static async Task ExitDuringThrow()
    {
        var (pet, time, _) = NewPet();
        await BeginUiThrow(pet, time); pet.Close();
        Check(!pet.WasClosed, "Pet close skipped queued throw.");
        await Until(pet, () => pet.WasClosed);
        Check(pet.Behavior.CurrentSample.Kind == ClipKind.Idle && pet.CareState.LastGameRewardUtc is null, "Exit did not drain or awarded.");
    }

    private static async Task FullUiRound()
    {
        var (pet, time, directory) = NewPet();
        try
        {
            double initialMood = pet.CareState.Mood;
            await BeginUiThrow(pet, time);
            var window = pet.GameWindow!;
            Check(((TextBlock)window.FindName("StageText")).Text == "出拳！", "Result revealed before throw.");
            time.Advance(RockPaperScissorsGame.ThrowSeconds); window.Refresh();
            Check(((TextBlock)window.FindName("StageText")).Text == "出拳！", "Time alone bypassed animation acknowledgement.");
            await Drive(pet, window.PendingOperation); window.Refresh();
            await Drive(pet, window.PendingOperation);
            time.Advance(RockPaperScissorsGame.ReactionSeconds); window.Refresh();
            Check(pet.CareState.LastGameRewardUtc == time.GetUtcNow() && pet.CareState.Mood > initialMood + 1.9, "Complete game did not save mood reward.");
            var saved = new PetStore(directory).Load();
            Check(saved is not null && saved.LastGameRewardUtc == pet.CareState.LastGameRewardUtc && saved.Mood == pet.CareState.Mood, "Mood/cursor were not saved together.");
            Check(pet.CareState.Experience == 0 && pet.CareState.Coins == 0, "Game generated XP or currency.");
        }
        finally { await Close(pet); }
    }

    private static async Task RewardCooldown()
    {
        var (pet, time, directory) = NewPet();
        try
        {
            await pet.OpenGameAsync(); Guid first = await Prepare(pet); await CompletePresentation(pet, first);
            Check(pet.Reward(new(first, 2, time.GetUtcNow())), "First reward denied.");
            Check(!pet.Reward(new(first, 2, time.GetUtcNow())), "Duplicate claim rewarded.");
            Guid second = await Prepare(pet); await CompletePresentation(pet, second);
            Check(!pet.Reward(new(second, 2, time.GetUtcNow())), "Cooldown bypassed.");
            Check(new PetStore(directory).Load()?.LastGameRewardUtc == time.GetUtcNow(), "Cooldown absent on disk.");
            time.Advance(300); Guid third = await Prepare(pet); await CompletePresentation(pet, third);
            Check(pet.Reward(new(third, 2, time.GetUtcNow())), "Five-minute cooldown never expired.");
        }
        finally { await Close(pet); }
    }

    private static async Task RewardClockRollback()
    {
        var (pet, time, _) = NewPet();
        try
        {
            await pet.OpenGameAsync(); Guid first = await Prepare(pet); await CompletePresentation(pet, first);
            pet.CareState.LastGameRewardUtc = time.GetUtcNow().AddHours(1);
            Check(!pet.Reward(new(first, 2, time.GetUtcNow())), "Future durable reward was rebased.");
            pet.CareState.LastGameRewardUtc = null;
            Guid second = await Prepare(pet); await CompletePresentation(pet, second);
            time.JumpUtc(-600);
            Check(!pet.Reward(new(second, 2, time.GetUtcNow())) && pet.CareState.LastGameRewardUtc is null, "Backwards care high-water cursor awarded.");
        }
        finally { await Close(pet); }
    }

    private static async Task RewardSaveFailure()
    {
        var (pet, time, directory) = NewPet();
        try
        {
            await pet.OpenGameAsync(); Guid round = await Prepare(pet); await CompletePresentation(pet, round);
            Check(pet.Persist(), "Initial isolated save failed.");
            var other = new PetStore(directory); var otherState = other.Load()!; otherState.Mood = 20;
            Check(other.Save(otherState), "Competing isolated instance could not save.");
            double mood = pet.CareState.Mood;
            Check(!pet.Reward(new(round, 2, time.GetUtcNow())) && pet.CareState.Mood == mood && pet.CareState.LastGameRewardUtc is null,
                "Failed atomic save mutated reward on live state.");
            Check(!pet.Reward(new(round, 2, time.GetUtcNow())) && new PetStore(directory).Load()!.Mood == 20, "Failed reward retried or overwrote another instance.");
        }
        finally { await Close(pet); }
    }

    private static async Task InvalidPresentations()
    {
        var (pet, time, _) = NewPet();
        try
        {
            await pet.OpenGameAsync(); Guid first = await Prepare(pet);
            await ExpectFailure(pet.Perform(new(Guid.NewGuid(), RpsCue.Throw, RpsChoice.Rock)));
            Check(!pet.Reward(new(first, 2, time.GetUtcNow())), "Stale callback rewarded.");
            Guid second = await Prepare(pet);
            await ExpectFailure(pet.Perform(new(second, RpsCue.React, RpsChoice.Rock, RpsOutcome.PetWin)));
            Guid third = await Prepare(pet); pet.ForgetReadyClip(ClipKind.RpsRock);
            await ExpectFailure(pet.Perform(new(third, RpsCue.Throw, RpsChoice.Rock)));
            Check(pet.Behavior.PendingCount == 0 && !pet.Reward(new(third, 2, time.GetUtcNow())), "Missing frames silently played idle or rewarded.");
        }
        finally { await Close(pet); }
    }
}
