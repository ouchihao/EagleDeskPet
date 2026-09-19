using System.IO;
using System.Text.Json;
using System.Windows;
using DuckDeskPet;
using DuckDeskPet.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) ||
            !File.Exists(Path.Combine(args[0], "DuckDeskPet", "PetWindow.Feeding.cs")))
        { Console.Error.WriteLine("Usage: FeedingIntegrationSelfTest <absolute repository root>"); return 2; }
        string root = Path.Combine(Path.GetTempPath(), "EagleFeedingIntegration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var results = new List<object>(); int failures = 0;
        var cases = new (string Name, Action Body)[]
        {
            ("Read-only admission rejects paused, working and duplicate reactions", Admission),
            ("A full equal-priority queue rejects food before any debit", FullQueue),
            ("A disposable lower-priority item permits one new meal", EvictableQueue),
            ("An existing queued Eat cannot authorize another consumable", CoalescedQueue),
            ("Queued idle is not completion and start requires a new Eat sequence", Sequence),
            ("Two seconds later during an earlier reaction cannot debit a second meal", RepeatedMeal),
            ("Work and pause cannot discard a consumed but queued meal", WorkPause),
            ("A previous Eat completion cannot complete a newly queued meal", PreviousEat),
            ("Earlier same-priority reactions finish before the owned Eat", EarlierReaction),
            ("Fullness, missing food and cooldown release failed feeding admission", CareRejections),
            ("Hungry scene defers and coalesces feeding without consuming", Hungry),
            ("Save conflict preserves disk while one in-memory meal finishes", SaveFailure),
            ("Close waits through the queued meal then retries at its full end", Closing),
            ("A subsequent completed meal is allowed without changing cooldown", NextMeal),
            ("Production work, pause, game and equipment guards include feeding", Wiring),
        };
        foreach (var (name, body) in cases)
        {
            try { body(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex); }
        }
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = failures == 0, cases = results }, new JsonSerializerOptions { WriteIndented = true }));
        app.Shutdown();
        Console.WriteLine($"Feeding integration: {cases.Length - failures}/{cases.Length}; production partial/Core/store, controlled clock/render shell. Evidence: {root}");
        return failures == 0 ? 0 : 1;

        PetWindow Pet() => new(Path.Combine(root, Guid.NewGuid().ToString("N")));
        void WithPet(Action<PetWindow> body)
        {
            var pet = Pet();
            try { body(pet); }
            finally { pet.FinishAndClose(); }
        }
        void Admission()
        {
            var b = new PetBehaviorController();
            Check(b.PreviewReaction(PetBehaviorKind.Fed) == PetBehaviorRequestResult.Queued && b.PendingCount == 0, "Preview mutated queue.");
            b.SetPaused(true); Check(b.PreviewReaction(PetBehaviorKind.Fed) == PetBehaviorRequestResult.RejectedPaused, "Paused accepted.");
            b.SetPaused(false); b.SetWorkState(true, false); Check(b.PreviewReaction(PetBehaviorKind.Fed) == PetBehaviorRequestResult.RejectedWorking, "Work accepted.");
            b.SetWorkState(false, false); b.QueueReaction(PetBehaviorKind.Fed);
            Check(b.PreviewReaction(PetBehaviorKind.Fed) == PetBehaviorRequestResult.Coalesced && b.PendingCount == 1, "Duplicate changed queue.");
        }
        void FullQueue() => WithPet(p =>
        {
            foreach (var clip in new[] { ClipKind.Tea, ClipKind.Annoyed, ClipKind.Yawn, ClipKind.Shy, ClipKind.Eat, ClipKind.RpsRock, ClipKind.RpsPaper, ClipKind.RpsScissors })
                Check(p.Behavior.RequestAction(clip) == PetBehaviorRequestResult.Queued, "Fixture queue failed.");
            Check(p.Behavior.PreviewReaction(PetBehaviorKind.Fed) == PetBehaviorRequestResult.RejectedFull, "Full admitted.");
            p.FeedPet(); Check(p.CareState.Food == 10 && p.CareState.TotalMeals == 0 && !p.IsFeeding && p.Behavior.PendingCount == 8, "Full queue consumed food or changed requests.");
        });
        void EvictableQueue() => WithPet(p =>
        {
            foreach (var clip in new[] { ClipKind.Tea, ClipKind.Annoyed, ClipKind.Yawn, ClipKind.Shy, ClipKind.RpsRock, ClipKind.RpsPaper, ClipKind.RpsScissors }) p.Behavior.RequestAction(clip);
            p.Behavior.QueueReaction(PetBehaviorKind.Notification);
            Check(p.Behavior.PreviewReaction(PetBehaviorKind.Fed) == PetBehaviorRequestResult.Queued && p.Behavior.PendingCount == 8, "Preview discarded an item.");
            p.FeedPet(); Check(p.IsFeeding && p.CareState.Food == 9 && p.Behavior.PendingCount == 8, "Safe eviction failed.");
            p.RenderUntil(() => !p.IsFeeding);
        });
        void CoalescedQueue() => WithPet(p =>
        {
            p.Behavior.QueueReaction(PetBehaviorKind.Fed); p.FeedPet();
            Check(!p.IsFeeding && p.CareState.Food == 10 && p.CareState.TotalMeals == 0, "Coalescing spent a meal.");
        });
        void Sequence() => WithPet(p =>
        {
            long before = p.Behavior.CurrentSample.Sequence;
            p.FeedPet(); p.RenderStep();
            Check(p.IsFeeding && p.Behavior.CurrentSample.Kind == ClipKind.Idle && p.CareState.Food == 9, "Idle was considered complete.");
            p.RenderUntil(() => p.Behavior.CurrentSample.Kind == ClipKind.Eat);
            Check(p.IsFeeding && p.Behavior.CurrentSample.Sequence > before, "New Eat not seen.");
            p.RenderUntil(() => p.Behavior.CurrentSample.Progress >= .9);
            Check(p.IsFeeding, "Released before complete endpoint.");
            p.RenderUntil(() => !p.IsFeeding);
            Check(p.Behavior.CurrentSample.Kind == ClipKind.Idle && p.CareState.TotalMeals == 1, "Meal failed final boundary.");
        });
        void RepeatedMeal() => WithPet(p =>
        {
            p.Behavior.RequestAction(ClipKind.Shy); p.RenderUntil(() => p.Behavior.CurrentSample.Kind == ClipKind.Shy);
            p.FeedPet(); p.Time.Advance(2.1);
            for (int i = 0; i < 126; i++) p.RenderStep();
            Check(p.Behavior.CurrentSample.Kind == ClipKind.Idle && p.IsFeeding, "Fixture not in pre-Eat idle.");
            p.FeedPet(); Check(p.CareState.Food == 9 && p.CareState.TotalMeals == 1 && p.IsFeeding, "Second debit after care cooldown.");
            p.RenderUntil(() => !p.IsFeeding);
        });
        void WorkPause() => WithPet(p =>
        {
            p.FeedPet(); Check(!p.TryWork() && !p.TryPause() && !p.Behavior.IsPaused && !p.CareState.IsWorking, "Work/pause stole queued meal.");
            p.RenderUntil(() => p.Behavior.CurrentSample.Kind == ClipKind.Eat);
            Check(!p.TryWork() && !p.TryPause(), "Work/pause stole active meal.");
            p.RenderUntil(() => !p.IsFeeding); Check(p.TryPause(), "Pause stayed blocked after full meal.");
        });
        void PreviousEat() => WithPet(p =>
        {
            p.Behavior.RequestAction(ClipKind.Eat); p.RenderUntil(() => p.Behavior.CurrentSample.Kind == ClipKind.Eat);
            long old = p.Behavior.CurrentSample.Sequence; p.FeedPet();
            p.RenderUntil(() => p.Behavior.CurrentSample.Kind == ClipKind.Idle);
            Check(p.IsFeeding, "Older sequence released new meal.");
            p.RenderUntil(() => p.Behavior.CurrentSample.Kind == ClipKind.Eat && p.Behavior.CurrentSample.Sequence > old);
            Check(p.IsFeeding, "New sequence released early."); p.RenderUntil(() => !p.IsFeeding);
        });
        void EarlierReaction() => WithPet(p =>
        {
            p.Behavior.QueueReaction(PetBehaviorKind.Annoyed); p.FeedPet();
            p.RenderUntil(() => p.Behavior.CurrentSample.Kind == ClipKind.Annoyed);
            Check(p.IsFeeding, "Another reaction displaced ownership."); p.RenderUntil(() => !p.IsFeeding);
            Check(p.CareState.Food == 9, "Wrong debit count.");
        });
        void CareRejections()
        {
            WithPet(p => { p.CareState.Fullness = 100; p.FeedPet(); Check(!p.IsFeeding && p.CareState.Food == 10 && p.Behavior.PendingCount == 0, "Full pet claimed animation."); });
            WithPet(p => { p.CareState.Food = 0; p.FeedPet(); Check(!p.IsFeeding && p.Behavior.PendingCount == 0, "Empty pantry claimed animation."); });
            WithPet(p => { p.CareState.LastFedUtc = p.Time.GetUtcNow(); p.FeedPet(); Check(!p.IsFeeding && p.CareState.Food == 10 && p.Behavior.PendingCount == 0, "Care cooldown claimed animation."); });
        }
        void Hungry() => WithPet(p =>
        {
            p.Behavior.RequestHungryScene(); p.RenderUntil(() => p.Behavior.IsHungrySceneActive);
            p.FeedPet(); p.FeedPet(); p.FeedPet();
            Check(p.PendingFeed && !p.IsFeeding && p.CareState.Food == 10, "Scene deferral consumed food.");
            p.RenderUntil(() => !p.Behavior.IsHungrySceneActive); p.FeedPet();
            Check(p.IsFeeding && !p.PendingFeed && p.CareState.Food == 9, "Deferred meal admission failed.");
            p.RenderUntil(() => !p.IsFeeding);
        });
        void SaveFailure() => WithPet(p =>
        {
            Directory.CreateDirectory(p.DirectoryPath); string path = Path.Combine(p.DirectoryPath, "pet-state.json");
            File.WriteAllText(path, "{\"external\":true}"); string disk = File.ReadAllText(path);
            p.FeedPet(); Check(p.IsFeeding && p.CareState.Food == 9 && File.ReadAllText(path) == disk, "CAS failure overwrote disk or dropped meal.");
            p.RenderUntil(() => !p.IsFeeding); Check(p.CareState.Coins == 0 && p.CareState.TotalMeals == 1, "Save failure duplicated state.");
        });
        void Closing() => WithPet(p =>
        {
            p.FeedPet(); p.Close(); PetWindow.DrainDispatcher();
            Check(!p.WasClosed && p.IsFeeding, "Close cut queued meal.");
            p.RenderUntil(() => !p.IsFeeding); PetWindow.DrainDispatcher();
            Check(p.WasClosed && p.Behavior.CurrentSample.Kind == ClipKind.Idle, "Close did not retry after complete meal.");
        });
        void NextMeal() => WithPet(p =>
        {
            p.FeedPet(); p.RenderUntil(() => !p.IsFeeding); p.Time.Advance(4.1); p.FeedPet();
            Check(p.IsFeeding && p.CareState.Food == 8 && p.CareState.TotalMeals == 2, "Next meal stayed locked.");
            p.RenderUntil(() => !p.IsFeeding); Check(PetCareService.FeedCooldownSeconds == 2, "Cooldown was changed.");
        });
        void Wiring()
        {
            string care = File.ReadAllText(Path.Combine(args[0], "DuckDeskPet", "PetWindow.Care.cs"));
            string main = File.ReadAllText(Path.Combine(args[0], "DuckDeskPet", "PetWindow.xaml.cs"));
            string content = File.ReadAllText(Path.Combine(args[0], "DuckDeskPet", "PetWindow.ContentResources.cs"));
            string play = File.ReadAllText(Path.Combine(args[0], "DuckDeskPet", "PetWindow.Play.cs"));
            Check(care.Split('\n').Single(x => x.Contains("internal bool InteractionsUnavailable =>")).Contains("IsFeeding"), "Interaction guard missing.");
            Check(content.Split('\n').Single(x => x.Contains("internal bool IsContentInteractionBusy =>")).Contains("IsFeeding"), "Equipment guard missing.");
            Check(main.Contains("TickFeeding();") && main.Contains("TryCloseFeedingForPetExit()") &&
                main.Split('\n').Any(x => x.Contains("if (_pauseChanging") && x.Contains("IsFeeding")), "Host feed tick/close/pause missing.");
            Check(care.Contains("CanStartWork => !InteractionsUnavailable") && play.Contains("InteractionsUnavailable || IsContentInteractionBusy"), "Work/game guard missing.");
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
