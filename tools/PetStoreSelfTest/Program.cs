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
        Check(care.State.Food == 5 && care.State.Fullness == 75 && care.State.Level == 1, "Default game state changed.");
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
        Check(current.Version == PetState.CurrentVersion && current.Food == 4 && current.Fullness == 100 && current.Experience == 5,
            "Current save fields did not round-trip.");
        Check(current.TotalMeals == 1 && current.Achievements.SequenceEqual(new[] { "first-meal" }), "Meal and achievement were lost.");
        PetState previous = JsonSerializer.Deserialize<PetState>(File.ReadAllBytes(StatePath + ".bak"))!;
        Check(previous.Food == 5 && previous.TotalMeals == 0 && previous.Experience == 0, "Backup is not the pre-feed state.");
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
