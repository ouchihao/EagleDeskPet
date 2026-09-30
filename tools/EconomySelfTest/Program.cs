using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DuckDeskPet;
using DuckDeskPet.Core;

internal static class Program
{
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);
    private static string _directory = "";

    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--worker") return Worker(args[1], args[2]);
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) ||
            !File.Exists(Path.Combine(args[0], "DuckDeskPet", "PetStore.cs")))
        {
            Console.Error.WriteLine("Usage: EconomySelfTest <absolute project root>");
            return 2;
        }
        string root = Path.Combine(Path.GetFullPath(args[0]), ".codex-build", "economy-test-" + Guid.NewGuid().ToString("N"));
        Console.WriteLine("Isolated output: " + root);
        Directory.CreateDirectory(root);
        var cases = new (string Name, Action Test)[]
        {
            ("new-wallet-zero", () => { var c = Care(); Equal(4, c.State.Version); Equal(0, c.State.Coins); }),
            ("run-income-is-independent-of-shopping", () => {
                var c = Care(); c.StartWork(Start); c.Advance(Start.AddMinutes(3));
                Equal(2.54m, c.EarnedCoinsThisRun); c.State.Coins -= 2;
                Equal(2.54m, c.EarnedCoinsThisRun); c.StopWork(Start.AddMinutes(3));
                c.Advance(Start.AddMinutes(4)); Equal(2.54m, c.EarnedCoinsThisRun);
                var restarted = new PetCareService(c.State.CreateSnapshot(), Start.AddMinutes(4));
                Equal(0, (int)restarted.EarnedCoinsThisRun); Equal(0.54m, restarted.State.Coins);
            }),
            ("idle-does-not-earn", () => { var c = Care(); c.Advance(Start.AddHours(2)); Equal(0, c.State.Coins); }),
            ("whole-minute-boundary", MinuteBoundary),
            ("tick-cadence-independent", TickCadence),
            ("cancel-retains-fraction", CancelRetainsFraction),
            ("mood-zero-crossing-is-piecewise", MoodZeroCrossing),
            ("low-mood-keeps-working", () => { var c = Working(0.2); c.Advance(Start.AddHours(2)); Equal(60, c.State.Coins); Check(c.State.IsWorking); }),
            ("zero-mood-earns-half-rate", () => { var c = Working(0); c.Advance(Start.AddHours(1)); Equal(30, c.State.Coins); }),
            ("offline-two-hour-cap", OfflineCap),
            ("offline-idle-no-wage", () => { var c = new PetCareService(new PetState { LastUpdatedUtc = Start }, Start.AddDays(7)); Equal(0, c.State.Coins); }),
            ("repeated-timestamp-no-double", RepeatedTimestamp),
            ("work-cursor-no-double", WorkCursor),
            ("clock-rollback-in-process", ClockRollback),
            ("clock-rollback-restart", ClockRollbackRestart),
            ("clock-rollback-does-not-repeat-mood-decay", ClockRollbackMood),
            ("v1-no-retroactive-wages", Migration),
            ("v1-clears-wallet-preserves-ledger", MigrationInjectedWallet),
            ("v1-future-clock-no-retro", MigrationFutureClock),
            ("cap-consumes-excess-work", WageCap),
            ("wallet-values-normalized", NormalizeWallet),
            ("snapshot-is-detached", Snapshot),
            ("fraction-save-reload", SaveFraction),
            ("v1-store-migration-backup", MigrationStore),
            ("v1-bom-load-preserved", LegacyBom),
            ("unknown-version-retained", () => RejectFile("{\"Version\":999,\"Coins\":99}")),
            ("invalid-json-retained", () => RejectFile("{ nope")),
            ("invalid-ledger-retained", () => RejectFile("{\"Version\":2,\"AppliedWalletDebits\":{\"bad id\":5}}")),
            ("debit-and-mutation-one-save", DebitAndMutation),
            ("duplicate-debit-after-restart", DuplicateDebit),
            ("id-reuse-different-amount", ReusedId),
            ("insufficient-funds-no-mutation", Insufficient),
            ("invalid-debits-rejected", InvalidDebits),
            ("mutation-rejection-no-write", () => RejectMutation(c => { c.Food++; return false; })),
            ("mutation-exception-no-write", () => RejectMutation(c => { c.Food++; throw new InvalidOperationException("fixture"); })),
            ("mutation-cannot-change-wallet", () => RejectMutation(c => { c.Coins++; return true; })),
            ("mutation-cannot-change-wage-remainder", () => RejectMutation(c => { c.WageRemainderUnits += 1m; return true; })),
            ("mutation-cannot-rewind-wage-cursor", () => RejectMutation(c => { c.WageSettledWorkSeconds = -1; return true; })),
            ("mutation-cannot-reroll-or-shorten-loafing", () => RejectMutation(c => { c.LoafingRandomState = 17; c.LoafingRemainingSeconds = 1m; return true; })),
            ("mutation-cannot-remove-receipt", () => RejectMutation(c => { c.AppliedWalletDebits.Clear(); return true; })),
            ("mutation-invalid-list-no-write", () => RejectMutation(c => { c.Achievements = null!; return true; })),
            ("mutation-retained-reference-detached", RetainedCandidate),
            ("failed-save-no-debit-or-grant", FailedSave),
            ("ledger-full-preserves-replay", FullLedger),
            ("stale-instance-no-overwrite", StaleInstance),
            ("unread-existing-save-retained", UnreadExisting),
            ("two-processes-one-settlement", TwoProcesses),
            ("equipped-rates-and-hard-caps", EquipmentRates),
            ("ownership-pending-preview-and-fallback-no-boost", InactiveEquipment),
            ("mid-minute-equip-settles-old-rate", MidMinuteEquipment),
            ("weighted-money-and-xp-split-restart-invariant", WeightedCadence),
            ("geared-zero-crossing-and-equipment-change-cadence", GearedZeroCrossing),
            ("equipment-reduces-both-idle-and-work-decay", EquipmentDecay),
            ("equipment-offline-cap-still-two-hours", EquipmentOffline),
            ("v2-migration-clears-money-preserves-receipts-ownership-and-honors", VersionTwoMigration),
            ("v3-clears-once-and-v4-reopen-keeps-new-money", VersionThreeMigration),
            ("fractional-debit-exact-and-idempotent", DecimalDebit),
            ("overprecision-debit-and-save-rejected", OverPrecision),
            ("catalog-prices-are-exponential", CatalogPrices),
            ("level-costs-grow-and-progress-is-explicit", GrowthCurve),
            ("legacy-level-conversion-is-bounded-and-one-time", LegacyGrowthMigration),
            ("seven-day-price-baseline-without-random-breaks", ShoppingRouteHours),
            ("deferred-startup-settles-once-at-actual-equipment-rate", DeferredCatchUp),
            ("deferred-startup-fallback-and-rollback-keep-highwater", DeferredFallback),
        };
        int failures = 0;
        var results = new List<object>();
        foreach (var item in cases)
        {
            _directory = Path.Combine(root, item.Name);
            Directory.CreateDirectory(_directory);
            try { item.Test(); Console.WriteLine("PASS " + item.Name); results.Add(new { name = item.Name, passed = true }); }
            catch (Exception ex)
            {
                failures++; Console.WriteLine("FAIL " + item.Name + ": " + ex);
                results.Add(new { name = item.Name, passed = false, error = ex.ToString() });
            }
        }
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { testCount = cases.Length, failures, results },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{cases.Length - failures}/{cases.Length} economy checks passed. No normal user data/config was read or changed.");
        return failures == 0 ? 0 : 1;
    }

    private static PetCareService Care() => new(null, Start, loafingRandomStep: NoBreak);
    private static ulong NoBreak(ulong _) => 99UL;
    private static PetCareService Working(double mood = 70)
    {
        var c = new PetCareService(new PetState { Mood = mood, LastUpdatedUtc = Start }, Start, loafingRandomStep: NoBreak);
        c.StartWork(Start);
        return c;
    }
    private static string SavePath => Path.Combine(_directory, "pet-state.json");
    private static PetState Wallet(int coins = 10) => new() { Coins = coins, LastUpdatedUtc = Start, WageLastUpdatedUtc = Start };
    private static PetStore Store() => new(_directory);

    private static void MinuteBoundary()
    {
        var c = Working(); c.Advance(Start.AddMilliseconds(59900)); Equal(0.84m, c.State.Coins);
        c.Advance(Start.AddSeconds(60)); Equal(0.84m, c.State.Coins);
        c.Advance(Start.AddSeconds(121)); Equal(1.71m, c.State.Coins);
    }
    private static void TickCadence()
    {
        var slow = Working(); var fast = Working(); slow.Advance(Start.AddSeconds(600));
        for (int i = 1; i <= 6000; i++) fast.Advance(Start.AddMilliseconds(i * 100));
        Equal(8.45m, slow.State.Coins); Equal(slow.State.Coins, fast.State.Coins);
        Equal(slow.State.MoodUnits, fast.State.MoodUnits); Equal(slow.State.WageRemainderScaledUnits, fast.State.WageRemainderScaledUnits);
    }
    private static void CancelRetainsFraction()
    {
        var c = Working(); c.StopWork(Start.AddSeconds(40)); decimal fraction = c.State.WageRemainderScaledUnits;
        c.Advance(Start.AddMinutes(5)); Equal(0.56m, c.State.Coins); Equal(fraction, c.State.WageRemainderScaledUnits);
        c.StartWork(Start.AddMinutes(5)); c.Advance(Start.AddSeconds(320)); Equal(0.84m, c.State.Coins);
    }
    private static void MoodZeroCrossing()
    {
        var slow = Working(5); var fast = Working(5);
        slow.Advance(Start.AddHours(1));
        for (int i = 1; i <= 3600; i++) fast.Advance(Start.AddSeconds(i));
        Equal(30.37m, slow.State.Coins); Equal(slow.State.Coins, fast.State.Coins);
        Equal(slow.State.WageRemainderScaledUnits, fast.State.WageRemainderScaledUnits);
        Near(3600, slow.State.TotalWorkSeconds); Check(slow.State.IsWorking); Near(0, slow.State.Mood);
    }
    private static void OfflineCap()
    {
        var before = Working(); var c = new PetCareService(before.State, Start.AddDays(7), loafingRandomStep: NoBreak);
        Equal(96, c.State.Coins); Near(7200, c.State.TotalWorkSeconds); Near(50, c.State.Mood);
        var reopened = new PetCareService(c.State, Start.AddDays(7)); Equal(96, reopened.State.Coins);
        reopened.Advance(Start.AddDays(7).AddMinutes(1)); Equal(96.74m, reopened.State.Coins);
    }
    private static void RepeatedTimestamp()
    {
        var c = Working(); c.Advance(Start.AddMinutes(1));
        for (int i = 0; i < 100; i++) Check(!c.Advance(Start.AddMinutes(1)));
        Equal(0.84m, c.State.Coins);
    }
    private static void WorkCursor()
    {
        var state = Wallet(0); state.TotalWorkSeconds = 75;
        Equal(1.25m, EconomyPolicy.SettleWages(state, 75)); Near(0, state.WageProgressSeconds);
        Equal(0, EconomyPolicy.SettleWages(state, 75)); Near(0, state.WageProgressSeconds);
        state.TotalWorkSeconds = 60; Equal(0, EconomyPolicy.SettleWages(state, 60)); Near(75, state.WageSettledWorkSeconds);
        state.TotalWorkSeconds = 120; Equal(0.75m, EconomyPolicy.SettleWages(state, 45)); Near(0, state.WageProgressSeconds);
    }
    private static void ClockRollback()
    {
        var c = Working(); c.Advance(Start.AddMinutes(2)); c.Advance(Start.AddMinutes(-10));
        c.Advance(Start.AddMinutes(2)); Equal(1.69m, c.State.Coins); c.Advance(Start.AddMinutes(3)); Equal(2.54m, c.State.Coins);
    }
    private static void ClockRollbackRestart()
    {
        var before = Working(); before.Advance(Start.AddMinutes(10)); Equal(8.45m, before.State.Coins);
        var c = new PetCareService(before.State, Start); c.Advance(Start.AddMinutes(5)); Equal(8.45m, c.State.Coins);
        var reopened = new PetCareService(c.State, Start.AddMinutes(5)); reopened.Advance(Start.AddMinutes(10)); Equal(8.45m, reopened.State.Coins);
        reopened.Advance(Start.AddMinutes(11)); Equal(9.29m, reopened.State.Coins);
        before.Advance(Start.AddMinutes(11)); Equal(before.State.Coins, reopened.State.Coins); Equal(before.State.MoodUnits, reopened.State.MoodUnits);
    }
    private static void ClockRollbackMood()
    {
        var state = Wallet(); state.IsWorking = true; state.Mood = 0.1;
        state.LastUpdatedUtc = Start.AddMinutes(5); state.WageLastUpdatedUtc = state.LastUpdatedUtc;
        var c = new PetCareService(state, Start); c.Advance(Start.AddMinutes(5)); Near(0.1, c.State.Mood); Equal(10m, c.State.Coins);
        c.Advance(Start.AddMinutes(10)); Equal(12.50m, c.State.Coins); Check(c.State.IsWorking);
    }
    private static void Migration()
    {
        var v1 = new PetState { Version = 1, IsWorking = true, LastUpdatedUtc = Start, TotalWorkSeconds = 3600,
            WorkExperienceProgressSeconds = 59, Food = 7, Experience = 100, TotalMeals = 3, TotalPets = 12, Achievements = new() { "first-meal" } };
        var c = new PetCareService(v1, Start.AddHours(1), loafingRandomStep: NoBreak); Equal(4, c.State.Version); Equal(0, c.State.Coins);
        Near(7200, c.State.WageSettledWorkSeconds); Near(0, c.State.WageProgressSeconds); Equal(3, c.State.TotalMeals); Equal(12, c.State.TotalPets);
        Check(c.State.Achievements.Contains("first-meal")); Equal(1, v1.Version);
        c.Advance(Start.AddHours(1).AddSeconds(59)); Equal(0.78m, c.State.Coins);
        c.Advance(Start.AddHours(1).AddMinutes(1)); Equal(0.79m, c.State.Coins);
    }
    private static void MigrationInjectedWallet()
    {
        var c = new PetCareService(new PetState { Version = 1, LastUpdatedUtc = Start, TotalWorkSeconds = 90000,
            Coins = 9999, WageProgressSeconds = 59, AppliedWalletDebits = new() { ["old"] = 5 } }, Start);
        Equal(0, c.State.Coins); Near(0, c.State.WageProgressSeconds); Near(90000, c.State.WageSettledWorkSeconds); Equal(5m, c.State.AppliedWalletDebits["old"]);
    }
    private static void MigrationFutureClock()
    {
        var c = new PetCareService(new PetState { Version = 1, LastUpdatedUtc = Start.AddYears(1), TotalWorkSeconds = 500, IsWorking = true }, Start);
        Equal(0, c.State.Coins); c.Advance(Start.AddMinutes(1)); Equal(0.84m, c.State.Coins);
    }
    private static void WageCap()
    {
        var c = Working(); c.State.Coins = EconomyPolicy.MaximumCoins - 1;
        c.Advance(Start.AddSeconds(135)); Equal(EconomyPolicy.MaximumCoins, c.State.Coins); Near(0, c.State.WageProgressSeconds);
        c.State.Coins -= 5; c.Advance(Start.AddSeconds(135)); Equal(EconomyPolicy.MaximumCoins - 5, c.State.Coins);
        c.Advance(Start.AddSeconds(180)); Equal(EconomyPolicy.MaximumCoins - 4.37m, c.State.Coins);
    }
    private static void NormalizeWallet()
    {
        var c = new PetCareService(new PetState { LastUpdatedUtc = Start, Coins = int.MaxValue,
            WageProgressSeconds = double.NaN, WageSettledWorkSeconds = double.PositiveInfinity, TotalWorkSeconds = 123 }, Start);
        Equal(EconomyPolicy.MaximumCoins, c.State.Coins); Near(0, c.State.WageProgressSeconds); Near(123, c.State.WageSettledWorkSeconds);
        Equal(0, new PetCareService(new PetState { Coins = -1, LastUpdatedUtc = Start }, Start).State.Coins);
    }
    private static void Snapshot()
    {
        var s = Wallet(); s.AppliedWalletDebits.Add("old", 1); s.Achievements.Add("first-meal");
        var copy = s.CreateSnapshot(); copy.AppliedWalletDebits.Clear(); copy.Achievements.Clear(); Equal(1, s.AppliedWalletDebits.Count); Equal(1, s.Achievements.Count);
        var applied = new PetState(); applied.ApplySnapshot(s); s.AppliedWalletDebits.Clear(); s.Achievements.Clear(); Equal(1, applied.AppliedWalletDebits.Count); Equal(1, applied.Achievements.Count);
    }
    private static void SaveFraction()
    {
        var c = Working(); c.Advance(Start.AddSeconds(45.25)); Check(Store().Save(c.State));
        var c2 = new PetCareService(Store().Load(), Start.AddSeconds(45.25)); Equal(c.State.WageRemainderScaledUnits, c2.State.WageRemainderScaledUnits); Equal(0.64m, c2.State.Coins);
        c2.Advance(Start.AddMinutes(1)); Equal(0.84m, c2.State.Coins); Near(60, c2.State.WageSettledWorkSeconds);
    }
    private static void MigrationStore()
    {
        string original = "{\"Version\":1,\"LastUpdatedUtc\":\"2026-09-19T00:00:00+00:00\",\"Food\":9,\"Experience\":120,\"TotalWorkSeconds\":1200}";
        File.WriteAllText(SavePath, original); var store = Store(); var saved = store.Load(); Equal(1, saved!.Version);
        Equal(original, File.ReadAllText(SavePath)); var c = new PetCareService(saved, Start); Check(store.Save(c.State));
        Equal(original, File.ReadAllText(store.MigrationBackupPath!)); Equal(4, Store().Load()!.Version); Equal(9, c.State.Food); Equal(130, c.State.Experience); Equal(0, c.State.Coins);
    }
    private static void RejectFile(string text)
    {
        File.WriteAllText(SavePath, text); var store = Store(); Check(store.Load() is null); Check(!store.CanSave); Check(!store.Save(Wallet()));
        Equal(text, File.ReadAllText(SavePath)); Check(!string.IsNullOrEmpty(store.Warning));
    }
    private static void LegacyBom()
    {
        byte[] bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"Version\":1,\"Coins\":999}")).ToArray();
        File.WriteAllBytes(SavePath, bytes); var store = Store(); var state = store.Load(); Equal(1, state!.Version);
        Check(File.ReadAllBytes(SavePath).SequenceEqual(bytes)); var care = new PetCareService(state, Start); Equal(0, care.State.Coins);
        Check(store.Save(care.State)); Check(File.ReadAllBytes(SavePath + ".bak").SequenceEqual(bytes));
    }
    private static void DebitAndMutation()
    {
        var store = Store(); var state = Wallet(); Check(store.Save(state));
        var result = store.TryTransaction(state, "purchase-1", 3, c => { c.Food++; return true; }); Equal(WalletTransactionStatus.Applied, result.Status);
        Equal(7, state.Coins); Equal(6, state.Food); var saved = Store().Load()!; Equal(7, saved.Coins); Equal(6, saved.Food); Equal(3, saved.AppliedWalletDebits["purchase-1"]);
        var old = JsonSerializer.Deserialize<PetState>(File.ReadAllText(SavePath + ".bak"))!; Equal(10, old.Coins); Equal(5, old.Food);
    }
    private static void DuplicateDebit()
    {
        var store = Store(); var state = Wallet(); Check(store.Save(state)); Check(store.TryDebit(state, "same", 3).Success);
        var reopened = Store(); var saved = reopened.Load()!; string bytes = File.ReadAllText(SavePath); bool invoked = false;
        var result = reopened.TryTransaction(saved, "same", 3, c => { invoked = true; c.Food++; return true; });
        Equal(WalletTransactionStatus.AlreadyApplied, result.Status); Check(!invoked); Equal(7, saved.Coins); Equal(bytes, File.ReadAllText(SavePath));
    }
    private static void ReusedId()
    {
        var store = Store(); var state = Wallet(); Check(store.Save(state)); Check(store.TryDebit(state, "same", 3).Success);
        Equal(WalletTransactionStatus.InvalidRequest, store.TryDebit(state, "same", 4).Status); Equal(7, state.Coins);
    }
    private static void Insufficient()
    {
        var store = Store(); var state = Wallet(1); Check(store.Save(state)); bool invoked = false;
        Equal(WalletTransactionStatus.InsufficientFunds, store.TryTransaction(state, "large", 2, c => { invoked = true; return true; }).Status);
        Check(!invoked); Equal(1, state.Coins); Equal(0, state.AppliedWalletDebits.Count);
    }
    private static void InvalidDebits()
    {
        var store = Store(); var state = Wallet();
        foreach (int amount in new[] { 0, -1, int.MaxValue }) Equal(WalletTransactionStatus.InvalidRequest, store.TryDebit(state, "id", amount).Status);
        foreach (string id in new[] { "", " ", "bad/id", new string('x', 81) }) Equal(WalletTransactionStatus.InvalidRequest, store.TryDebit(state, id, 1).Status);
        Equal(10, state.Coins); Check(!File.Exists(SavePath));
    }
    private static void RejectMutation(Func<PetState, bool> mutate)
    {
        var store = Store(); var state = Wallet(); Check(store.Save(state)); string before = File.ReadAllText(SavePath);
        Equal(WalletTransactionStatus.InvalidRequest, store.TryTransaction(state, "reject", 1, mutate).Status);
        Equal(10, state.Coins); Equal(5, state.Food); Equal(0, state.AppliedWalletDebits.Count); Equal(before, File.ReadAllText(SavePath));
    }
    private static void FailedSave()
    {
        var store = Store(); var state = Wallet(); Check(store.Save(state)); byte[] original = File.ReadAllBytes(SavePath);
        using (var locked = new FileStream(SavePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Equal(WalletTransactionStatus.SaveFailed, store.TryTransaction(state, "retry", 3, c => { c.Food++; return true; }).Status);
            Equal(10, state.Coins); Equal(5, state.Food); Equal(0, state.AppliedWalletDebits.Count);
            Check(File.ReadAllBytes(SavePath).SequenceEqual(original)); Check(!string.IsNullOrWhiteSpace(store.Warning));
            Equal(0, Directory.GetFiles(_directory, "*.tmp").Length);
        }
        Check(store.TryTransaction(state, "retry", 3, c => { c.Food++; return true; }).Success); Equal(7, state.Coins); Equal(6, state.Food);
    }
    private static void RetainedCandidate()
    {
        var store = Store(); var state = Wallet(); Check(store.Save(state)); PetState? retained = null;
        Check(store.TryTransaction(state, "grant", 1, c => { retained = c; c.Achievements.Add("first-meal"); return true; }).Success);
        retained!.Achievements.Clear(); retained.AppliedWalletDebits.Clear(); retained.Coins = 999;
        Equal(9, state.Coins); Equal(1, state.AppliedWalletDebits.Count); Equal(1, state.Achievements.Count);
        var disk = Store().Load()!; Equal(9, disk.Coins); Equal(1, disk.AppliedWalletDebits.Count); Equal(1, disk.Achievements.Count);
    }
    private static void FullLedger()
    {
        var state = Wallet(); for (int i = 0; i < EconomyPolicy.MaximumWalletTransactions; i++) state.AppliedWalletDebits.Add("receipt-" + i, 1);
        var store = Store(); Check(store.Save(state)); Check(new FileInfo(SavePath).Length < 1_048_576);
        Equal(WalletTransactionStatus.LedgerFull, store.TryDebit(state, "new", 1).Status);
        Equal(WalletTransactionStatus.AlreadyApplied, store.TryDebit(state, "receipt-0", 1).Status); Equal(10, state.Coins);
    }
    private static void StaleInstance()
    {
        var first = Store(); var second = Store(); var c1 = new PetCareService(first.Load(), Start); var c2 = new PetCareService(second.Load(), Start);
        c1.StartWork(Start); c2.StartWork(Start); c1.Advance(Start.AddMinutes(1)); c2.Advance(Start.AddMinutes(1));
        Check(first.Save(c1.State)); Check(!second.Save(c2.State)); Check(!second.CanSave); Equal(0.84m, Store().Load()!.Coins);
    }
    private static void UnreadExisting()
    {
        Check(Store().Save(Wallet(99))); var unopened = Store(); Check(!unopened.Save(Wallet(0))); Equal(99, Store().Load()!.Coins);
    }
    private static void TwoProcesses()
    {
        var state = Working().State; Check(Store().Save(state));
        using var a = StartWorker("a"); using var b = StartWorker("b");
        try
        {
            Check(SpinWait.SpinUntil(() => File.Exists(Path.Combine(_directory, "a.ready")) && File.Exists(Path.Combine(_directory, "b.ready")), 10000));
            File.WriteAllText(Path.Combine(_directory, "go"), "go");
            Check(a.WaitForExit(10000) && b.WaitForExit(10000)); Equal(0, a.ExitCode); Equal(0, b.ExitCode);
            var results = new[] { File.ReadAllText(Path.Combine(_directory, "a.result")), File.ReadAllText(Path.Combine(_directory, "b.result")) };
            Equal(1, results.Count(x => x == "saved")); Equal(1, results.Count(x => x == "conflict")); Equal(0.84m, Store().Load()!.Coins);
        }
        finally { if (!a.HasExited) a.Kill(); if (!b.HasExited) b.Kill(); }
    }
    private static Process StartWorker(string token)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--worker"); start.ArgumentList.Add(_directory); start.ArgumentList.Add(token);
        return Process.Start(start) ?? throw new InvalidOperationException("No worker process.");
    }
    private static int Worker(string directory, string token)
    {
        if (!Path.IsPathFullyQualified(directory) || !directory.Contains(Path.DirectorySeparatorChar + ".codex-build" + Path.DirectorySeparatorChar) || token is not ("a" or "b")) return 2;
        var store = new PetStore(directory); var saved = store.Load(); if (saved is null) return 3;
        var c = new PetCareService(saved, Start); c.Advance(Start.AddMinutes(1));
        File.WriteAllText(Path.Combine(directory, token + ".ready"), "ready");
        if (!SpinWait.SpinUntil(() => File.Exists(Path.Combine(directory, "go")), 10000)) return 4;
        File.WriteAllText(Path.Combine(directory, token + ".result"), store.Save(c.State) ? "saved" : "conflict");
        return 0;
    }
    private static PetState Geared(params string[] ids)
    {
        var state = Wallet(0); state.Food = PetCareService.MaximumFood;
        foreach (string id in ids)
        {
            state.Content.OwnedContentIds.Add(id);
            state.Content.Equipped[ContentCatalog.Get(id).Slot!.Value] = id;
        }
        return state;
    }
    private static void EquipmentRates()
    {
        var care = new PetCareService(Geared(ContentCatalog.OfficeOutfitId, ContentCatalog.BoardroomDeskId,
            ContentCatalog.UltrabookComputerId), Start);
        Equal(new EquipmentBonuses(0.5m, 2m, 1.6m), care.CurrentBonuses);
        Equal(2.55m, care.MoneyPerWorkMinute); Equal(2.6m, care.WorkExperiencePerMinute);
        Equal(new EquipmentBonuses(0.5m, 2.5m, 2m), EquipmentPolicy.Clamp(new(9m, 9m, 9m)));
        Equal(EquipmentBonuses.None, EquipmentPolicy.Clamp(new(-1m, -1m, -1m)));
    }
    private static void InactiveEquipment()
    {
        var state = Wallet(0);
        state.Content.Equipped[ContentSlot.Outfit] = ContentCatalog.OfficeOutfitId;
        var unowned = new PetCareService(state, Start); Equal(0.85m, unowned.MoneyPerWorkMinute);
        unowned.SetEffectiveEquipment(Start, new Dictionary<ContentSlot, string> { [ContentSlot.Outfit] = ContentCatalog.OfficeOutfitId });
        unowned.State.Content.OwnedContentIds.Add(ContentCatalog.OfficeOutfitId);
        Equal(0.85m, unowned.MoneyPerWorkMinute); // acquiring later cannot activate a rejected selection
        state.Content.OwnedContentIds.Add(ContentCatalog.OfficeOutfitId);
        var absent = new PetCareService(state, Start, x => x.IsDefault); Equal(0.85m, absent.MoneyPerWorkMinute);
        state.Content.Equipped[ContentSlot.Outfit] = ContentCatalog.DefaultOutfitId;
        state.Content.PendingEquipment[ContentSlot.Outfit] = ContentCatalog.OfficeOutfitId;
        var pending = new PetCareService(state, Start); pending.StartWork(Start); pending.Advance(Start.AddMinutes(1));
        Equal(0.84m, pending.State.Coins); Equal(EquipmentBonuses.None, pending.CurrentBonuses);
        // Even changing a preview/candidate's selection cannot mutate the active snapshot.
        pending.State.Content.Equipped[ContentSlot.Outfit] = ContentCatalog.OfficeOutfitId;
        pending.Advance(Start.AddMinutes(2)); Equal(1.69m, pending.State.Coins);
        pending.SetEffectiveEquipment(Start.AddMinutes(2), new Dictionary<ContentSlot, string>
            { [ContentSlot.Desk] = ContentCatalog.OfficeOutfitId });
        Equal(EquipmentBonuses.None, pending.CurrentBonuses); // wrong-slot injection
    }
    private static void MidMinuteEquipment()
    {
        var care = new PetCareService(Geared(), Start); care.StartWork(Start);
        care.State.Content.OwnedContentIds.Add(ContentCatalog.OfficeOutfitId);
        care.State.Content.Equipped[ContentSlot.Outfit] = ContentCatalog.OfficeOutfitId;
        care.SetEffectiveEquipment(Start.AddSeconds(20), care.State.Content.Equipped);
        Equal(0.28m, care.State.Coins);
        care.Advance(Start.AddSeconds(40)); Equal(0.84m, care.State.Coins);
        Equal(0, care.State.Experience); Equal(56m, care.State.WorkExperienceRemainderUnits);
        care.State.Content.Equipped[ContentSlot.Outfit] = ContentCatalog.DefaultOutfitId;
        care.SetEffectiveEquipment(Start.AddSeconds(50), care.State.Content.Equipped);
        care.Advance(Start.AddSeconds(60)); Equal(1.27m, care.State.Coins);
        Equal(1, care.State.Experience); Equal(24m, care.State.WorkExperienceRemainderUnits);
    }
    private static void WeightedCadence()
    {
        var initial = Geared(ContentCatalog.MintDeskId, ContentCatalog.MidnightComputerId, ContentCatalog.HoodieOutfitId);
        var slow = new PetCareService(initial.CreateSnapshot(), Start); slow.StartWork(Start);
        var fast = new PetCareService(initial.CreateSnapshot(), Start); fast.StartWork(Start);
        // 100 ns timestamps include fractional-cent and fractional-XP boundaries.
        const long totalTicks = 617_1234567L;
        slow.Advance(Start.AddTicks(totalTicks));
        var random = new Random(174); long elapsed = 0; int steps = 0;
        while (elapsed < totalTicks)
        {
            elapsed = Math.Min(totalTicks, elapsed + random.NextInt64(1, 4_000_000));
            fast.Advance(Start.AddTicks(elapsed));
            if (++steps % 97 == 0)
            {
                var roundtrip = JsonSerializer.Deserialize<PetState>(JsonSerializer.Serialize(fast.State));
                fast = new PetCareService(roundtrip, Start.AddTicks(elapsed));
            }
        }
        Equal(slow.State.Coins, fast.State.Coins);
        Equal(slow.State.WageRemainderUnits, fast.State.WageRemainderUnits);
        Equal(slow.State.Experience, fast.State.Experience);
        Equal(slow.State.WorkExperienceRemainderUnits, fast.State.WorkExperienceRemainderUnits);
        Check(EconomyPolicy.HasCentPrecision(fast.State.Coins));
    }
    private static void EquipmentDecay()
    {
        var state = Geared(ContentCatalog.OfficeOutfitId, ContentCatalog.BoardroomDeskId);
        var working = new PetCareService(state.CreateSnapshot(), Start); working.StartWork(Start);
        working.Advance(Start.AddHours(1)); Near(65, working.State.Mood);
        var idle = new PetCareService(state.CreateSnapshot(), Start); idle.Advance(Start.AddHours(1));
        Near(69.5, idle.State.Mood); Equal(0m, idle.State.Coins);
        state.Mood = 0; var tired = new PetCareService(state, Start); tired.StartWork(Start);
        tired.Advance(Start.AddHours(1)); Near(3600, tired.State.TotalWorkSeconds);
        Equal(72m, tired.State.Coins); Check(tired.State.IsWorking);
    }
    private static void GearedZeroCrossing()
    {
        var initial = Geared(ContentCatalog.MintDeskId, ContentCatalog.HoodieOutfitId);
        initial.Mood = .527123;
        var slow = new PetCareService(initial.CreateSnapshot(), Start); slow.StartWork(Start);
        var fast = new PetCareService(initial.CreateSnapshot(), Start); fast.StartWork(Start);
        var plain = new Dictionary<ContentSlot, string>();
        slow.State.Content.Equipped.Clear();
        slow.SetEffectiveEquipment(Start.AddSeconds(120.1234567), plain);
        slow.Advance(Start.AddHours(1));
        var random = new Random(775); long ticks = 0; const long switchTicks = 1_201_234567;
        while (ticks < TimeSpan.TicksPerHour)
        {
            long next = Math.Min(TimeSpan.TicksPerHour, ticks + random.NextInt64(1, 10_000_000));
            if (ticks < switchTicks && next >= switchTicks)
            {
                fast.State.Content.Equipped.Clear();
                fast.SetEffectiveEquipment(Start.AddTicks(switchTicks), plain);
                next = switchTicks;
            }
            else fast.Advance(Start.AddTicks(next));
            ticks = next;
            if (ticks % 17 == 0) fast = new PetCareService(JsonSerializer.Deserialize<PetState>(JsonSerializer.Serialize(fast.State)), Start.AddTicks(ticks));
        }
        Equal(slow.State.Coins, fast.State.Coins); Equal(slow.State.MoodUnits, fast.State.MoodUnits);
        Equal(slow.State.WageRemainderScaledUnits, fast.State.WageRemainderScaledUnits);
        Equal(slow.State.WorkExperienceRemainderUnits, fast.State.WorkExperienceRemainderUnits);
    }
    private static void EquipmentOffline()
    {
        var state = Geared(ContentCatalog.OfficeOutfitId, ContentCatalog.BoardroomDeskId, ContentCatalog.UltrabookComputerId);
        state.IsWorking = true;
        var care = new PetCareService(state, Start.AddDays(3), loafingRandomStep: NoBreak); Equal(297m, care.State.Coins);
        Near(7200, care.State.TotalWorkSeconds); Equal(312, care.State.Experience);
        var sameTime = new PetCareService(care.State.CreateSnapshot(), Start.AddDays(3)); Equal(297m, sameTime.State.Coins);
        var rollback = new PetCareService(sameTime.State.CreateSnapshot(), Start);
        rollback.Advance(Start.AddMinutes(1)); Equal(297m, rollback.State.Coins);
    }
    private static void VersionTwoMigration()
    {
        const string old = "{\"Version\":2,\"Coins\":123,\"WageProgressSeconds\":30,\"TotalWorkSeconds\":90000," +
            "\"WageSettledWorkSeconds\":90000,\"LastUpdatedUtc\":\"2026-09-19T00:00:00Z\",\"WageLastUpdatedUtc\":\"2026-09-19T00:00:00Z\"," +
            "\"AppliedWalletDebits\":{\"purchase:outfit.office\":40},\"Content\":{\"OwnedContentIds\":[\"outfit.office\"]}," +
            "\"Achievements\":[\"meals-gold\",\"growth-gold\"]}";
        File.WriteAllText(SavePath, old); var store = Store(); var care = new PetCareService(store.Load(), Start);
        Equal(4, care.State.Version); Equal(0m, care.State.Coins); Equal(0m, care.State.WageRemainderUnits);
        Equal(40m, care.State.AppliedWalletDebits["purchase:outfit.office"]);
        Check(care.State.Content.OwnedContentIds.Contains(ContentCatalog.OfficeOutfitId));
        Check(care.State.Achievements.Contains("meals-gold") && care.State.Achievements.Contains("growth-gold"));
        Check(store.Save(care.State)); Equal(old, File.ReadAllText(SavePath + ".bak"));
        var again = new PetCareService(Store().Load(), Start); Equal(0m, again.State.Coins);
        again.StartWork(Start); again.Advance(Start.AddMinutes(1)); Equal(0.84m, again.State.Coins);
    }
    private static void DecimalDebit()
    {
        var state = Wallet(); var store = Store(); Check(store.Save(state));
        Check(store.TryDebit(state, "fraction", 0.29m).Changed); Equal(9.71m, state.Coins);
        Equal(0.29m, Store().Load()!.AppliedWalletDebits["fraction"]);
        Equal(WalletTransactionStatus.AlreadyApplied, store.TryDebit(state, "fraction", 0.29m).Status);
        Equal(9.71m, state.Coins);
    }
    private static void OverPrecision()
    {
        var state = Wallet(); var store = Store(); Check(store.Save(state));
        Equal(WalletTransactionStatus.InvalidRequest, store.TryDebit(state, "fraction", 0.001m).Status);
        state.Coins = 1.001m; Check(!store.Save(state)); Equal(10m, Store().Load()!.Coins);
        Check(!EconomyPolicy.HasCentPrecision(0.001m)); Check(EconomyPolicy.HasCentPrecision(decimal.MaxValue));
    }
    private static void CatalogPrices()
    {
        Check(ContentCatalog.Definitions.Where(x => x.Type == ContentType.Computer && !x.IsDefault).Select(x => x.Price).Order()
            .SequenceEqual(new decimal[] { 5100, 10200, 20400, 40800, 81600, 163200 }));
        Check(ContentCatalog.Definitions.Where(x => x.Type == ContentType.Desk && !x.IsDefault).Select(x => x.Price).Order()
            .SequenceEqual(new decimal[] { 6375, 12750, 25500, 51000, 102000, 204000 }));
        Check(ContentCatalog.Definitions.Where(x => x.Type == ContentType.Outfit && !x.IsDefault).Select(x => x.Price).Order()
            .SequenceEqual(new decimal[] { 25500, 51000, 102000, 204000, 408000 }));
        Equal(408000m, ContentCatalog.Get(ContentCatalog.OfficeOutfitId).Price);
        Check(ContentCatalog.Definitions.Where(x => x.IsDefault).All(x => x.Price == 0 && x.Bonuses == EquipmentBonuses.None));
    }
    private static void GrowthCurve()
    {
        int last = 0;
        for (int level = 1; level < 1000; level++)
        {
            int cost = GrowthPolicy.RequirementForLevel(level); Check(cost > last); last = cost;
            Equal(level, GrowthPolicy.LevelForExperience(GrowthPolicy.ExperienceAtLevel(level)));
            Equal(level, GrowthPolicy.LevelForExperience(GrowthPolicy.ExperienceAtLevel(level + 1) - 1));
        }
        var state = new PetState { Experience = 200 };
        Equal(2, state.Level); Equal(100, state.ExperienceIntoLevel); Equal(150, state.NextLevelRequirement);
        Equal(50, state.ExperienceToNextLevel); Near(2.0 / 3.0, state.LevelProgress);
        Equal(1000, GrowthPolicy.LevelForExperience(int.MaxValue)); Equal(1, GrowthPolicy.LevelForExperience(-100));
        var maximum = new PetState { Experience = PetCareService.MaximumExperience };
        Check(maximum.IsMaximumLevel); Equal(0, maximum.ExperienceToNextLevel); Equal(0, maximum.NextLevelRequirement); Near(1, maximum.LevelProgress);
        Equal(int.MaxValue, GrowthPolicy.ExperienceAtLevel(int.MaxValue));
    }
    private static void LegacyGrowthMigration()
    {
        foreach (int version in new[] { 1, 2 })
        foreach (int xp in new[] { -1, 0, 1, 99, 100, 120, 199, 249, 900, 999, 5000, 99899, 99900, int.MaxValue })
        {
            var old = Wallet(123); old.Version = version; old.Experience = xp;
            old.Achievements.Add("growth-gold"); old.Content.OwnedContentIds.Add(ContentCatalog.OfficeOutfitId);
            var care = new PetCareService(old, Start);
            int clampedOld = Math.Clamp(xp, 0, GrowthPolicy.LegacyMaximumExperience);
            Equal(1 + clampedOld / 100, care.State.Level);
            Equal(GrowthPolicy.MigrateLegacyExperience(xp), care.State.Experience);
            if (!care.State.IsMaximumLevel)
                Near((clampedOld % 100) / 100.0, care.State.LevelProgress, 1.0 / care.State.NextLevelRequirement);
            Equal(0m, care.State.Coins);
            Check(care.State.Achievements.Contains("growth-gold"));
            Check(care.State.Content.OwnedContentIds.Contains(ContentCatalog.OfficeOutfitId));
            var restarted = new PetCareService(JsonSerializer.Deserialize<PetState>(JsonSerializer.Serialize(care.State)), Start);
            Equal(care.State.Experience, restarted.State.Experience); Equal(care.State.Level, restarted.State.Level);
            Equal(care.State.Coins, restarted.State.Coins);
        }
        Equal(2700, GrowthPolicy.MigrateLegacyExperience(900));
        Equal(25_024_950, GrowthPolicy.MigrateLegacyExperience(99_900));
    }
    private static void ShoppingRouteHours()
    {
        var care = Working();
        for (int minute = 1; minute <= 7 * 24 * 60; minute++) care.Advance(Start.AddMinutes(minute));
        Equal(5113.50m, care.State.Coins); Check(care.State.IsWorking); Near(0, care.State.Mood);
        decimal cheapest = ContentCatalog.Definitions.Where(x => !x.IsDefault).Min(x => x.Price);
        Equal(5100m, cheapest); Check(care.State.Coins >= cheapest && care.State.Coins < cheapest + 30m);
    }
    private static void DeferredCatchUp()
    {
        var state = Geared(ContentCatalog.OfficeOutfitId, ContentCatalog.BoardroomDeskId, ContentCatalog.UltrabookComputerId);
        state.IsWorking = true;
        var care = new PetCareService(state, Start.AddHours(1), deferInitialAdvance: true, loafingRandomStep: NoBreak);
        Check(care.IsCatchUpPending); Equal(EquipmentBonuses.None, care.CurrentBonuses);
        string before = JsonSerializer.Serialize(care.State);
        Check(!care.Advance(Start.AddHours(2)));
        Equal(PetCareStatus.Initializing, care.Feed(Start.AddHours(2)).Status);
        Equal(PetCareStatus.Initializing, care.Pet(Start.AddHours(2)).Status);
        Equal(PetCareStatus.Initializing, care.StartWork(Start.AddHours(2)).Status);
        Equal(PetCareStatus.Initializing, care.StopWork(Start.AddHours(2)).Status);
        Equal(before, JsonSerializer.Serialize(care.State));
        bool rejected = false;
        try { care.SetEffectiveEquipment(Start.AddHours(2), state.Content.Equipped); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected); Equal(before, JsonSerializer.Serialize(care.State));
        Check(care.BeginCatchUp(Start.AddHours(3), state.Content.Equipped));
        Check(!care.IsCatchUpPending); Equal(297m, care.State.Coins); Equal(312, care.State.Experience);
        Near(7200, care.State.TotalWorkSeconds);
        before = JsonSerializer.Serialize(care.State);
        Check(!care.BeginCatchUp(Start.AddHours(4), new Dictionary<ContentSlot, string>()));
        Equal(before, JsonSerializer.Serialize(care.State)); Equal(2.4m, care.MoneyPerWorkMinute);
    }
    private static void DeferredFallback()
    {
        var state = Geared(ContentCatalog.OfficeOutfitId); state.IsWorking = true;
        var absent = new PetCareService(state, Start.AddHours(1), deferInitialAdvance: true, loafingRandomStep: NoBreak);
        absent.BeginCatchUp(Start.AddHours(1), new Dictionary<ContentSlot, string>());
        Equal(49.5m, absent.State.Coins); Equal(0.8m, absent.MoneyPerWorkMinute);
        Check(absent.State.Content.OwnedContentIds.Contains(ContentCatalog.OfficeOutfitId));
        Equal(ContentCatalog.OfficeOutfitId, absent.State.Content.Equipped[ContentSlot.Outfit]);
        var rollback = new PetCareService(absent.State.CreateSnapshot(), Start, deferInitialAdvance: true);
        rollback.BeginCatchUp(Start.AddMinutes(30), state.Content.Equipped);
        Equal(49.5m, rollback.State.Coins); Equal(Start.AddHours(1), rollback.State.WageLastUpdatedUtc);
        rollback.Advance(Start.AddHours(1)); Equal(49.5m, rollback.State.Coins);
        rollback.Advance(Start.AddHours(1).AddMinutes(1)); Equal(51.09m, rollback.State.Coins);
    }
    private static void VersionThreeMigration()
    {
        var old = Wallet(321); old.Version = 3; old.WageRemainderUnits = .59m; old.WageProgressSeconds = 59;
        old.IsWorking = true; old.Experience = 1234; old.TotalWorkSeconds = 90000; old.WageSettledWorkSeconds = 1;
        old.AppliedWalletDebits.Add("already-paid", 3m);
        var migrated = new PetCareService(old, Start.AddHours(1), loafingRandomStep: NoBreak);
        Check(migrated.MigrationWasApplied); Equal(0m, migrated.State.Coins); Equal(0m, migrated.State.WageRemainderUnits);
        Equal(migrated.State.TotalWorkSeconds, migrated.State.WageSettledWorkSeconds);
        Equal(Start.AddHours(1), migrated.State.WageLastUpdatedUtc); Equal(1234 + 72, migrated.State.Experience);
        migrated.Advance(Start.AddHours(1).AddMinutes(1)); Equal(.79m, migrated.State.Coins);
        var again = new PetCareService(JsonSerializer.Deserialize<PetState>(JsonSerializer.Serialize(migrated.State)), Start.AddHours(1).AddMinutes(1));
        Check(!again.MigrationWasApplied); Equal(migrated.State.Coins, again.State.Coins);
        Equal(migrated.State.WageRemainderScaledUnits, again.State.WageRemainderScaledUnits);
        Equal(3m, again.State.AppliedWalletDebits["already-paid"]);
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, actual {actual}."); }
    private static void Near(double expected, double actual, double tolerance = 1e-7) { if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance) throw new InvalidOperationException($"Expected {expected}, actual {actual}."); }
}
