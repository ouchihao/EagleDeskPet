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
            ("new-wallet-zero", () => { var c = Care(); Equal(2, c.State.Version); Equal(0, c.State.Coins); }),
            ("idle-does-not-earn", () => { var c = Care(); c.Advance(Start.AddHours(2)); Equal(0, c.State.Coins); }),
            ("whole-minute-boundary", MinuteBoundary),
            ("tick-cadence-independent", TickCadence),
            ("cancel-retains-fraction", CancelRetainsFraction),
            ("hunger-cuts-off-fraction", HungerCutoff),
            ("hunger-exact-minute", () => { var c = Working(0.2); c.Advance(Start.AddHours(2)); Equal(1, c.State.Coins); Near(0, c.State.WageProgressSeconds); Check(!c.State.IsWorking); }),
            ("empty-stomach-no-wage", () => { var c = Working(0); c.Advance(Start.AddHours(1)); Equal(0, c.State.Coins); }),
            ("offline-two-hour-cap", OfflineCap),
            ("offline-idle-no-wage", () => { var c = new PetCareService(new PetState { LastUpdatedUtc = Start }, Start.AddDays(7)); Equal(0, c.State.Coins); }),
            ("repeated-timestamp-no-double", RepeatedTimestamp),
            ("work-cursor-no-double", WorkCursor),
            ("clock-rollback-in-process", ClockRollback),
            ("clock-rollback-restart", ClockRollbackRestart),
            ("clock-rollback-hunger-overlap", ClockRollbackHunger),
            ("v1-no-retroactive-wages", Migration),
            ("v1-ignores-wallet-fields", MigrationInjectedWallet),
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
            ("mutation-cannot-remove-receipt", () => RejectMutation(c => { c.AppliedWalletDebits.Clear(); return true; })),
            ("mutation-invalid-list-no-write", () => RejectMutation(c => { c.Achievements = null!; return true; })),
            ("mutation-retained-reference-detached", RetainedCandidate),
            ("failed-save-no-debit-or-grant", FailedSave),
            ("ledger-full-preserves-replay", FullLedger),
            ("stale-instance-no-overwrite", StaleInstance),
            ("unread-existing-save-retained", UnreadExisting),
            ("two-processes-one-settlement", TwoProcesses),
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

    private static PetCareService Care() => new(null, Start);
    private static PetCareService Working(double fullness = 75)
    {
        var c = new PetCareService(new PetState { Fullness = fullness, LastUpdatedUtc = Start }, Start);
        c.StartWork(Start);
        return c;
    }
    private static string SavePath => Path.Combine(_directory, "pet-state.json");
    private static PetState Wallet(int coins = 10) => new() { Coins = coins, LastUpdatedUtc = Start, WageLastUpdatedUtc = Start };
    private static PetStore Store() => new(_directory);

    private static void MinuteBoundary()
    {
        var c = Working(); c.Advance(Start.AddMilliseconds(59900)); Equal(0, c.State.Coins); Near(59.9, c.State.WageProgressSeconds);
        c.Advance(Start.AddSeconds(60)); Equal(1, c.State.Coins); Near(0, c.State.WageProgressSeconds);
        c.Advance(Start.AddSeconds(121)); Equal(2, c.State.Coins); Near(1, c.State.WageProgressSeconds);
    }
    private static void TickCadence()
    {
        var slow = Working(); var fast = Working(); slow.Advance(Start.AddSeconds(600));
        for (int i = 1; i <= 6000; i++) fast.Advance(Start.AddMilliseconds(i * 100));
        Equal(10, slow.State.Coins); Equal(slow.State.Coins, fast.State.Coins); Near(0, fast.State.WageProgressSeconds, 1e-6);
    }
    private static void CancelRetainsFraction()
    {
        var c = Working(); c.StopWork(Start.AddSeconds(40)); Near(40, c.State.WageProgressSeconds);
        c.Advance(Start.AddMinutes(5)); Equal(0, c.State.Coins);
        c.StartWork(Start.AddMinutes(5)); c.Advance(Start.AddSeconds(320)); Equal(1, c.State.Coins); Near(0, c.State.WageProgressSeconds);
    }
    private static void HungerCutoff()
    {
        var c = Working(0.3); c.Advance(Start.AddHours(2)); Equal(1, c.State.Coins); Near(30, c.State.WageProgressSeconds);
        Near(90, c.State.TotalWorkSeconds); Check(!c.State.IsWorking); c.Advance(Start.AddHours(3)); Equal(1, c.State.Coins);
    }
    private static void OfflineCap()
    {
        var before = Working(); var c = new PetCareService(before.State, Start.AddDays(7));
        Equal(120, c.State.Coins); Near(7200, c.State.TotalWorkSeconds); Near(51, c.State.Fullness);
        var reopened = new PetCareService(c.State, Start.AddDays(7)); Equal(120, reopened.State.Coins);
        reopened.Advance(Start.AddDays(7).AddMinutes(1)); Equal(121, reopened.State.Coins);
    }
    private static void RepeatedTimestamp()
    {
        var c = Working(); c.Advance(Start.AddMinutes(1));
        for (int i = 0; i < 100; i++) Check(!c.Advance(Start.AddMinutes(1)));
        Equal(1, c.State.Coins);
    }
    private static void WorkCursor()
    {
        var state = Wallet(0); state.TotalWorkSeconds = 75;
        Equal(1, EconomyPolicy.SettleWages(state, 75)); Near(15, state.WageProgressSeconds);
        Equal(0, EconomyPolicy.SettleWages(state, 75)); Near(15, state.WageProgressSeconds);
        state.TotalWorkSeconds = 60; Equal(0, EconomyPolicy.SettleWages(state, 60)); Near(75, state.WageSettledWorkSeconds);
        state.TotalWorkSeconds = 120; Equal(1, EconomyPolicy.SettleWages(state, 45)); Near(0, state.WageProgressSeconds);
    }
    private static void ClockRollback()
    {
        var c = Working(); c.Advance(Start.AddMinutes(2)); c.Advance(Start.AddMinutes(-10));
        c.Advance(Start.AddMinutes(2)); Equal(2, c.State.Coins); c.Advance(Start.AddMinutes(3)); Equal(3, c.State.Coins);
    }
    private static void ClockRollbackRestart()
    {
        var before = Working(); before.Advance(Start.AddMinutes(10)); Equal(10, before.State.Coins);
        var c = new PetCareService(before.State, Start); c.Advance(Start.AddMinutes(5)); Equal(10, c.State.Coins);
        var reopened = new PetCareService(c.State, Start.AddMinutes(5)); reopened.Advance(Start.AddMinutes(10)); Equal(10, reopened.State.Coins);
        reopened.Advance(Start.AddMinutes(11)); Equal(11, reopened.State.Coins);
    }
    private static void ClockRollbackHunger()
    {
        var state = Wallet(); state.IsWorking = true; state.Fullness = 0.1;
        state.LastUpdatedUtc = Start.AddMinutes(5); state.WageLastUpdatedUtc = state.LastUpdatedUtc;
        var c = new PetCareService(state, Start); c.Advance(Start.AddMinutes(10)); Equal(10, c.State.Coins); Check(!c.State.IsWorking);
    }
    private static void Migration()
    {
        var v1 = new PetState { Version = 1, IsWorking = true, LastUpdatedUtc = Start, TotalWorkSeconds = 3600,
            WorkExperienceProgressSeconds = 59, Food = 7, Experience = 100, TotalMeals = 3, TotalPets = 12, Achievements = new() { "first-meal" } };
        var c = new PetCareService(v1, Start.AddHours(1)); Equal(2, c.State.Version); Equal(0, c.State.Coins);
        Near(7200, c.State.WageSettledWorkSeconds); Near(0, c.State.WageProgressSeconds); Equal(3, c.State.TotalMeals); Equal(12, c.State.TotalPets);
        Check(c.State.Achievements.Contains("first-meal")); Equal(1, v1.Version);
        c.Advance(Start.AddHours(1).AddSeconds(59)); Equal(0, c.State.Coins);
        c.Advance(Start.AddHours(1).AddMinutes(1)); Equal(1, c.State.Coins);
    }
    private static void MigrationInjectedWallet()
    {
        var c = new PetCareService(new PetState { Version = 1, LastUpdatedUtc = Start, TotalWorkSeconds = 90000,
            Coins = 9999, WageProgressSeconds = 59, AppliedWalletDebits = new() { ["old"] = 5 } }, Start);
        Equal(0, c.State.Coins); Near(0, c.State.WageProgressSeconds); Near(90000, c.State.WageSettledWorkSeconds); Equal(0, c.State.AppliedWalletDebits.Count);
    }
    private static void MigrationFutureClock()
    {
        var c = new PetCareService(new PetState { Version = 1, LastUpdatedUtc = Start.AddYears(1), TotalWorkSeconds = 500, IsWorking = true }, Start);
        Equal(0, c.State.Coins); c.Advance(Start.AddMinutes(1)); Equal(1, c.State.Coins);
    }
    private static void WageCap()
    {
        var c = Working(); c.State.Coins = EconomyPolicy.MaximumCoins - 1;
        c.Advance(Start.AddSeconds(135)); Equal(EconomyPolicy.MaximumCoins, c.State.Coins); Near(15, c.State.WageProgressSeconds);
        c.State.Coins -= 5; c.Advance(Start.AddSeconds(135)); Equal(EconomyPolicy.MaximumCoins - 5, c.State.Coins);
        c.Advance(Start.AddSeconds(180)); Equal(EconomyPolicy.MaximumCoins - 4, c.State.Coins); Near(0, c.State.WageProgressSeconds);
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
        var c = Working(); c.Advance(Start.AddSeconds(45)); Check(Store().Save(c.State));
        var c2 = new PetCareService(Store().Load(), Start.AddSeconds(45)); Near(45, c2.State.WageProgressSeconds);
        c2.Advance(Start.AddMinutes(1)); Equal(1, c2.State.Coins); Near(60, c2.State.WageSettledWorkSeconds);
    }
    private static void MigrationStore()
    {
        string original = "{\"Version\":1,\"LastUpdatedUtc\":\"2026-09-19T00:00:00+00:00\",\"Food\":9,\"Experience\":120,\"TotalWorkSeconds\":1200}";
        File.WriteAllText(SavePath, original); var store = Store(); var saved = store.Load(); Equal(1, saved!.Version);
        Equal(original, File.ReadAllText(SavePath)); var c = new PetCareService(saved, Start); Check(store.Save(c.State));
        Equal(original, File.ReadAllText(SavePath + ".bak")); Equal(2, Store().Load()!.Version); Equal(9, c.State.Food); Equal(120, c.State.Experience); Equal(0, c.State.Coins);
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
        Check(first.Save(c1.State)); Check(!second.Save(c2.State)); Check(!second.CanSave); Equal(1, Store().Load()!.Coins);
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
            Equal(1, results.Count(x => x == "saved")); Equal(1, results.Count(x => x == "conflict")); Equal(1, Store().Load()!.Coins);
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
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, actual {actual}."); }
    private static void Near(double expected, double actual, double tolerance = 1e-7) { if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance) throw new InvalidOperationException($"Expected {expected}, actual {actual}."); }
}
