using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DuckDeskPet;
using DuckDeskPet.Core;

internal static class Program
{
    private static readonly DateTimeOffset TestTime = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
    private static string _runDirectory = string.Empty;

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: PetStoreSelfTest <absolute desktop-pet project directory>");
            return 2;
        }

        string workspace = Path.GetFullPath(args[0]);
        if (!Path.IsPathFullyQualified(args[0]) ||
            !File.Exists(Path.Combine(workspace, "DuckDeskPet", "PetStore.cs")) ||
            !File.Exists(Path.Combine(workspace, "DuckDeskPet.Core", "DuckDeskPet.Core.csproj")))
        {
            Console.Error.WriteLine("The supplied path is not the desktop-pet project directory.");
            return 2;
        }

        string parent = Path.GetFullPath(Path.Combine(workspace, ".codex-build", "pet-store-test"));
        _runDirectory = Path.Combine(parent, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        if (!_runDirectory.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test directory escaped its dedicated parent.");
        Console.WriteLine("Isolated test output: " + _runDirectory);
        Directory.CreateDirectory(_runDirectory);

        string? previousDirectory = Environment.GetEnvironmentVariable("EAGLE_PET_DATA_DIR");
        string sentinelDirectory = Path.Combine(_runDirectory, "isolated-control");
        Directory.CreateDirectory(sentinelDirectory);
        foreach (string name in new[] { "pet-state.json", "settings.json", "companion.json" })
        {
            foreach (string suffix in new[] { "", ".bak", ".tmp" })
                File.WriteAllText(Path.Combine(sentinelDirectory, name + suffix), "{\"fixture\":\"isolated-control-only\"}");
        }
        var sentinelBefore = SnapshotData(sentinelDirectory);
        var results = new List<object>();
        int failures = 0;
        try
        {
            Run("missing-default", MissingDefaults);
            Run("atomic-backup-roundtrip", AtomicBackupRoundTrip);
            Run("v1-v2-v3-protected-backups-survive-daily-saves", LegacyMigrationBackups);
            Run("migration-competing-reader-cannot-overwrite", ConcurrentMigration);
            Run("failed-migration-backup-prevents-replacement", FailedMigrationBackup);
            Run("loafing-survives-durable-save-without-new-migration", LoafingRoundTrip);
            Run("invalid-loafing-state-cannot-overwrite-save", InvalidLoafingState);
            Run("malformed-preserved", () => RejectedSavePreservesBytes(Encoding.UTF8.GetBytes("{ definitely not JSON")));
            Run("null-preserved", () => RejectedSavePreservesBytes(Encoding.UTF8.GetBytes("null")));
            Run("future-version-preserved", () => RejectedSavePreservesBytes(Encoding.UTF8.GetBytes("{\"Version\":999,\"Food\":40}")));
            Run("oversized-preserved", () =>
            {
                var bytes = new byte[1_048_577];
                Array.Fill(bytes, (byte)' ');
                bytes[0] = (byte)'{';
                bytes[^1] = (byte)'}';
                RejectedSavePreservesBytes(bytes);
            });

            var sentinelAfter = SnapshotData(sentinelDirectory);
            bool unchanged = sentinelBefore.OrderBy(pair => pair.Key).SequenceEqual(sentinelAfter.OrderBy(pair => pair.Key));
            if (!unchanged) failures++;
            results.Add(new { name = "isolated-control-unchanged", passed = unchanged });
            Console.WriteLine((unchanged ? "PASS " : "FAIL ") + "isolated-control-unchanged");
        }
        finally
        {
            Environment.SetEnvironmentVariable("EAGLE_PET_DATA_DIR", previousDirectory);
        }

        File.WriteAllText(Path.Combine(_runDirectory, "results.json"), JsonSerializer.Serialize(new
        {
            passed = failures == 0,
            testCount = results.Count,
            failures,
            isolatedDirectory = _runDirectory,
            isolatedControlDirectory = sentinelDirectory,
            results,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{results.Count - failures}/{results.Count} persistence regression checks passed.");
        Console.WriteLine("Only isolated fake files were read; normal user data was not accessed.");
        Console.WriteLine("Fixtures retained for inspection; no files were removed.");
        return failures == 0 ? 0 : 1;

        void Run(string name, Action body)
        {
            string directory = Path.Combine(_runDirectory, name);
            Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("EAGLE_PET_DATA_DIR", directory);
            try
            {
                Check(string.Equals(AppPaths.DataDirectory, directory, StringComparison.OrdinalIgnoreCase), "Data override was not honored.");
                body();
                Console.WriteLine("PASS " + name);
                results.Add(new { name, passed = true });
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + name + ": " + ex.Message);
                results.Add(new { name, passed = false, error = ex.ToString() });
            }
        }
    }

    private static void MissingDefaults()
    {
        var store = new PetStore();
        PetState? saved = store.Load();
        Check(saved is null, "Missing file did not return null.");
        Check(store.CanSave && store.Warning is null, "Missing file disabled saving.");
        var care = new PetCareService(saved, TestTime);
        Check(care.State.Food == 5 && care.State.Mood == 70 && care.State.Level == 1, "Default game state changed.");
        Check(!File.Exists(StatePath), "Read-only load created a save file.");
    }

    private static void AtomicBackupRoundTrip()
    {
        var store = new PetStore();
        var care = new PetCareService(null, TestTime);
        Check(store.Save(care.State), "First save failed: " + store.Warning);
        byte[] firstBytes = File.ReadAllBytes(StatePath);
        Check(!File.Exists(StatePath + ".tmp"), "First save left its temp file behind.");
        Check(care.Feed(TestTime).Success, "Test feed was rejected.");
        Check(store.Save(care.State), "Second save failed: " + store.Warning);
        Check(File.ReadAllBytes(StatePath + ".bak").SequenceEqual(firstBytes), "Backup does not match the complete prior save.");
        Check(!File.Exists(StatePath + ".tmp"), "Replace left its temp file behind.");
        PetState current = new PetStore().Load() ?? throw new InvalidOperationException("Round-trip returned null.");
        Check(current.Version == PetState.CurrentVersion && current.Food == 4 && current.Mood == 90 && current.Experience == 0,
            "Current save fields did not round-trip.");
        Check(current.TotalMeals == 1 && current.Achievements.SequenceEqual(new[] { "first-meal" }), "Meal and achievement were lost.");
        PetState previous = JsonSerializer.Deserialize<PetState>(File.ReadAllBytes(StatePath + ".bak"))!;
        Check(previous.Food == 5 && previous.TotalMeals == 0 && previous.Experience == 0, "Backup is not the pre-feed state.");
    }

    private static void LegacyMigrationBackups()
    {
        foreach (int version in new[] { 1, 2, 3 })
        {
            string directory = Path.Combine(AppPaths.DataDirectory, "v" + version);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "pet-state.json");
            byte[] original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(
                "{\"Version\":" + version + ",\"Fullness\":7,\"Mood\":70,\"Coins\":123.45,\"WageRemainderUnits\":0.59," +
                "\"Experience\":120,\"TotalWorkSeconds\":3600,\"AppliedWalletDebits\":{\"old-purchase\":5}," +
                "\"LastUpdatedUtc\":\"2026-09-05T00:00:00Z\"}")).ToArray();
            File.WriteAllBytes(path, original);
            var store = new PetStore(directory); var saved = store.Load();
            Check(saved?.Version == version && store.MigrationWasApplied, "Legacy version was not detected");
            Check(store.MigrationBackupPath is null && Directory.GetFiles(directory, "pet-state.pre-v4-*.json").Length == 0,
                "Read-only load claimed or created a migration backup");
            var care = new PetCareService(saved, TestTime);
            Check(care.MigrationWasApplied && care.State.Coins == 0m && care.State.WageRemainderUnits == 0m, "Wallet was not reset once");
            Check(care.State.AppliedWalletDebits["old-purchase"] == 5m, "Migration lost a transaction receipt");
            Check(store.Save(care.State), "Migration save failed");
            string backup = store.MigrationBackupPath!;
            Check(File.ReadAllBytes(backup).SequenceEqual(original), "Dedicated backup did not preserve exact BOM and original bytes");
            care.StartWork(TestTime); care.Advance(TestTime.AddMinutes(1));
            Check(store.Save(care.State) && store.Save(care.State), "Daily saves failed after migration");
            Check(File.ReadAllBytes(backup).SequenceEqual(original), "Daily .bak rotation overwrote the migration source");
            var reopenedStore = new PetStore(directory); var reopened = new PetCareService(reopenedStore.Load(), TestTime.AddMinutes(1));
            Check(!reopened.MigrationWasApplied && !reopenedStore.MigrationWasApplied, "v4 was migrated twice");
            Check(reopened.State.Coins == .84m && reopened.State.WageRemainderScaledUnits == care.State.WageRemainderScaledUnits, "New earnings were reset or rounded on reopen");
            Check(!File.ReadAllText(path).Contains("Fullness", StringComparison.Ordinal), "Removed fullness field survived serialization");
        }
    }

    private static void ConcurrentMigration()
    {
        byte[] original = Encoding.UTF8.GetBytes("{\"Version\":3,\"Coins\":50}");
        File.WriteAllBytes(StatePath, original);
        var first = new PetStore(); var stale = new PetStore();
        var state = new PetCareService(first.Load(), TestTime).State;
        var other = new PetCareService(stale.Load(), TestTime).State;
        Check(first.Save(state), "First migration did not commit");
        state.Coins = 7m; Check(first.Save(state), "Fresh earnings did not save");
        Check(!stale.Save(other) && !stale.CanSave, "Stale migrating writer erased new earnings");
        Check(new PetStore().Load()!.Coins == 7m, "Competing migration changed the wallet");
        Check(File.ReadAllBytes(first.MigrationBackupPath!).SequenceEqual(original), "Competing migration changed the protected backup");
    }

    private static void FailedMigrationBackup()
    {
        byte[] original = Encoding.UTF8.GetBytes("{\"Version\":3,\"Coins\":50}");
        File.WriteAllBytes(StatePath, original);
        var store = new PetStore(); var care = new PetCareService(store.Load(), TestTime);
        string expectedBackup = Path.Combine(AppPaths.DataDirectory, "pet-state.pre-v4-" +
            Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant() + ".json");
        Directory.CreateDirectory(expectedBackup);
        Check(!store.Save(care.State), "Save overwrote the old wallet without a protected backup");
        Check(File.ReadAllBytes(StatePath).SequenceEqual(original), "Failed backup changed original bytes");
        Check(!File.Exists(StatePath + ".bak"), "Failed backup attempted daily replacement");
    }

    private static void RejectedSavePreservesBytes(byte[] original)
    {
        File.WriteAllBytes(StatePath, original);
        var store = new PetStore();
        Check(store.Load() is null, "Rejected save was unexpectedly accepted.");
        Check(!store.CanSave && !string.IsNullOrWhiteSpace(store.Warning), "Rejected save did not lock writes with a warning.");
        Check(!store.Save(new PetCareService(null, TestTime).State), "Fallback save overwrote rejected input.");
        Check(File.ReadAllBytes(StatePath).SequenceEqual(original), "Rejected save bytes changed.");
        Check(!File.Exists(StatePath + ".tmp") && !File.Exists(StatePath + ".bak"), "Rejected save produced replacement files.");
    }

    private static void LoafingRoundTrip()
    {
        var care = new PetCareService(new PetState { LastUpdatedUtc = TestTime, WageLastUpdatedUtc = TestTime,
            LoafingRandomState = 1, Food = 0 }, TestTime, loafingRandomStep: _ => 100UL);
        care.StartWork(TestTime); care.Advance(TestTime.AddHours(1));
        var store = new PetStore(); Check(store.Save(care.State), "Active break did not save");
        var saved = new PetStore().Load()!;
        Check(saved.Version == 4 && saved.IsLoafing && saved.LoafingRemainingSeconds == 1800m,
            "Active break fields were not saved together");
        var resumed = new PetCareService(saved, TestTime.AddMinutes(70), loafingRandomStep: _ => throw new Exception("Unexpected reroll"));
        Check(!resumed.MigrationWasApplied && resumed.State.Coins == 49.5m, "v4 break caused a new economy migration");
        Check(resumed.State.LoafingRemainingSeconds == 1200m && resumed.State.Experience == 72 && resumed.State.Food == 14,
            "Restart reset the break or granted earnings during it");
        Check(resumed.State.Loafing == saved.Loafing && resumed.State.LoafingRandomState == saved.LoafingRandomState,
            "Restart selected another animation");
        var applied = new PetState(); applied.ApplySnapshot(resumed.State.CreateSnapshot());
        Check(applied.LoafingRemainingSeconds == 1200m && applied.LoafingRandomState == saved.LoafingRandomState,
            "Atomic snapshot application lost scheduler state");
    }

    private static void InvalidLoafingState()
    {
        var care = new PetCareService(null, TestTime); var store = new PetStore();
        Check(store.Save(care.State), "Initial save failed"); byte[] original = File.ReadAllBytes(StatePath);
        var invalid = care.State.CreateSnapshot(); invalid.Loafing = (LoafingKind)123;
        Check(!store.Save(invalid), "Unknown break kind was saved");
        invalid.Loafing = LoafingKind.None; invalid.LoafingRemainingSeconds = 1m;
        Check(!store.Save(invalid), "Orphaned break duration was saved");
        invalid.LoafingRemainingSeconds = 0m; invalid.WorkUntilLoafingCheckSeconds = decimal.MaxValue;
        Check(!store.Save(invalid), "Unbounded lottery countdown was saved");
        Check(File.ReadAllBytes(StatePath).SequenceEqual(original), "Rejected scheduler fields changed the save");
    }

    private static string StatePath => Path.Combine(AppPaths.DataDirectory, "pet-state.json");

    private static Dictionary<string, string> SnapshotData(string directory)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["directory-exists"] = Directory.Exists(directory).ToString(),
        };
        foreach (string name in new[] { "pet-state.json", "settings.json", "companion.json" })
        {
            foreach (string suffix in new[] { "", ".bak", ".tmp" })
            {
                string path = Path.Combine(directory, name + suffix);
                snapshot[name + suffix] = File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "absent";
            }
        }
        return snapshot;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
