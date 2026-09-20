using System.Text.Json;
using DuckDeskPet;
using DuckDeskPet.Core;

int passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
    Console.WriteLine("PASS " + message);
}
void Reject(Action action, string message)
{
    bool rejected = false;
    try { action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException) { rejected = true; }
    Check(rejected, message);
}

var now = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
var scheduler = new ReminderScheduler();
Check(scheduler.Items.Count == 0 && !scheduler.HasPending, "empty schedule");
var once = scheduler.Add(" 喝水 ", 1, false, now);
Check(once.Message == "喝水" && once.DueAtUtc == now.AddMinutes(1), "trimmed once-only UTC deadline");
Check(!scheduler.Advance(now.AddSeconds(59)) && scheduler.Items[0].Remaining(now).TotalSeconds == 60, "does not fire early");
Check(scheduler.Advance(now.AddMinutes(1)) && scheduler.Items[0].IsCompleted && scheduler.HasPending, "fires once at deadline");
Check(scheduler.AcknowledgePending(now.AddMinutes(1)).Count == 1 && !scheduler.HasPending, "one acknowledgement clears pending batch");
Check(!scheduler.Advance(now.AddDays(20)) && scheduler.AcknowledgePending(now.AddDays(20)).Count == 0, "completed reminder never repeats on later ticks");
Check(scheduler.TogglePause(once.Id, now.AddDays(20)) && scheduler.Items[0].DueAtUtc == now.AddDays(20).AddMinutes(1), "completed reminder can explicitly restart");

var repeated = new ReminderScheduler();
repeated.Add("伸个懒腰", 20, true, now);
Check(repeated.Advance(now.AddDays(5)) && repeated.Items[0].DueAtUtc == now.AddDays(5).AddMinutes(20), "offline missed repeat cycles coalesce and restart from now");
Check(!repeated.Advance(now.AddDays(5)) && repeated.AcknowledgePending(now.AddDays(5)).Count == 1, "no catch-up loop or replay flood");
Check(!repeated.Advance(now.AddDays(5).AddMinutes(19)), "fresh repeat interval preserved");
Check(repeated.Advance(now.AddDays(5).AddMinutes(20)), "repeat becomes due again next interval");
var reloaded = new ReminderScheduler(JsonSerializer.Deserialize<ReminderSnapshot>(JsonSerializer.Serialize(repeated.Snapshot())));
Check(reloaded.HasPending && reloaded.AcknowledgePending(now.AddDays(5)).Count == 1, "pending reminder survives restart exactly once");
Check(!new ReminderScheduler(reloaded.Snapshot()).HasPending, "acknowledgement survives restart");

var paused = new ReminderScheduler();
var pausable = paused.Add("小事", 10, true, now);
Check(paused.TogglePause(pausable.Id, now.AddMinutes(4)), "pause existing reminder");
Check(paused.Items[0].Remaining(now.AddDays(99)) == TimeSpan.FromMinutes(6) && !paused.Advance(now.AddDays(99)), "paused time frozen even offline");
var pausedReload = new ReminderScheduler(paused.Snapshot());
Check(pausedReload.TogglePause(pausable.Id, now.AddDays(99)) && pausedReload.Items[0].DueAtUtc == now.AddDays(99).AddMinutes(6), "resume retains exact remaining time after restart");
pausedReload.Advance(now.AddDays(100));
pausedReload.TogglePause(pausable.Id, now.AddDays(100));
Check(!pausedReload.HasPending && pausedReload.Items[0].IsPaused, "explicit pause clears unsaid repeat notification");
Check(!paused.Remove("missing") && !paused.TogglePause("missing", now), "unknown IDs are harmless");
Check(paused.Remove(pausable.Id) && paused.Items.Count == 0, "delete removes reminder");

foreach (int minutes in new[] { -1, 0, ReminderScheduler.MaximumMinutes + 1 })
    Reject(() => new ReminderScheduler().Add("x", minutes, false, now), "reject minutes " + minutes);
foreach (string message in new[] { "", "   ", "a\nb", "a\0b", new string('x', 121) })
    Reject(() => new ReminderScheduler().Add(message, 1, false, now), "reject blank/control/oversized message");
Reject(() => new ReminderScheduler().Add(null!, 1, false, now), "null message rejected safely");
var bounded = new ReminderScheduler();
for (int i = 0; i < ReminderScheduler.MaximumReminders; i++) bounded.Add("事情 " + i, 1, false, now);
Reject(() => bounded.Add("溢出", 1, false, now), "maximum eight reminders");
bounded.Advance(now.AddHours(4));
var batch = bounded.AcknowledgePending(now.AddHours(4));
Check(batch.Count == 8 && !bounded.HasPending && ReminderScheduler.BubbleMessage(batch).Contains("8 件事"), "many simultaneous overdue jobs yield one compact bubble");
Check(ReminderScheduler.BubbleMessage(batch).Length < 150, "coalesced bubble is bounded");
Check(ReminderScheduler.BubbleMessage(Array.Empty<LocalReminder>()) == "", "empty batch has no bubble");
var detached = bounded.Snapshot(); detached.Items.Clear();
Check(bounded.Items.Count == 8, "snapshot list cannot mutate live schedule");

var valid = new ReminderScheduler(); valid.Add("可持久化", 30, false, now);
var entry = valid.Items[0];
Reject(() => new ReminderScheduler(new() { Version = 2 }), "unknown state version rejected");
Reject(() => new ReminderScheduler(new() { Items = new() { entry, entry } }), "duplicate ID rejected");
Reject(() => new ReminderScheduler(new() { Items = new() { entry with { DueAtUtc = null } } }), "active reminder without deadline rejected");
Reject(() => new ReminderScheduler(new() { Items = new() { entry with { IsPaused = true, DueAtUtc = null, PausedRemainingTicks = -1 } } }), "negative paused time rejected");
Reject(() => new ReminderScheduler(new() { Items = new() { entry with { DueAtUtc = now.ToOffset(TimeSpan.FromHours(8)) } } }), "stored deadline must be UTC");

// Every filesystem test uses a uniquely owned OS temporary directory; no
// AppPaths, LocalApplicationData, pet-state.json or credential paths are read.
string temporaryRoot = Path.Combine(Path.GetTempPath(), "EagleReminderSelfTest-" + Guid.NewGuid().ToString("N"));
Console.WriteLine("ISOLATED TEST DIRECTORY " + temporaryRoot);
Directory.CreateDirectory(temporaryRoot);
try
{
    var fresh = new ReminderStore(Path.Combine(temporaryRoot, "fresh"));
    Check(fresh.Load().Items.Count == 0 && !Directory.Exists(Path.Combine(temporaryRoot, "fresh")), "empty read creates no files");
    Check(fresh.Save(valid.Snapshot()), "atomic initial save");
    string file = Path.Combine(temporaryRoot, "fresh", "reminders.json");
    var loadedStore = new ReminderStore(Path.Combine(temporaryRoot, "fresh"));
    Check(loadedStore.Load().Items.Single().Message == "可持久化", "saved message loads independently");
    var next = new ReminderScheduler(valid.Snapshot()); next.Add("第二条", 5, true, now);
    Check(fresh.Save(next.Snapshot()) && File.Exists(file + ".bak"), "replacement preserves a backup");
    string current = File.ReadAllText(file);
    Check(!loadedStore.Save(valid.Snapshot()) && File.ReadAllText(file) == current, "stale second process cannot overwrite current schedule");
    Check(!Directory.EnumerateFiles(Path.GetDirectoryName(file)!, "*.tmp").Any(), "no abandoned temp files after save");
    var unreadStore = new ReminderStore(Path.Combine(temporaryRoot, "fresh"));
    Check(!unreadStore.Save(new()) && File.ReadAllText(file) == current, "unobserved existing state never overwritten");

    foreach (var corruption in new[] { "{broken", "{\"Version\":9,\"Items\":[]}", new string('x', 65_537) })
    {
        string directory = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "reminders.json"); File.WriteAllText(path, corruption);
        var store = new ReminderStore(directory);
        Check(store.Load().Items.Count == 0 && store.Warning is not null && !store.Save(new()) && File.ReadAllText(path) == corruption,
            "corrupt/future/oversized file retained, never replaced with empty defaults");
    }
    string blocked = Path.Combine(temporaryRoot, "not-a-directory"); File.WriteAllText(blocked, "owned fixture");
    var failedStore = new ReminderStore(blocked); failedStore.Load();
    Check(!failedStore.Save(valid.Snapshot()) && failedStore.Warning is not null, "I/O failure is visible and does not pretend success");

    var pendingScheduler = new ReminderScheduler(); pendingScheduler.Add("重启只响一次", 1, false, now); pendingScheduler.Advance(now.AddHours(2));
    var pendingStore = new ReminderStore(Path.Combine(temporaryRoot, "pending")); pendingStore.Load(); pendingStore.Save(pendingScheduler.Snapshot());
    var restartStore = new ReminderStore(Path.Combine(temporaryRoot, "pending"));
    var restarted = new ReminderScheduler(restartStore.Load());
    Check(restarted.AcknowledgePending(now.AddHours(2)).Count == 1 && restartStore.Save(restarted.Snapshot()), "consume and persist one restart catch-up batch");
    Check(!new ReminderScheduler(new ReminderStore(Path.Combine(temporaryRoot, "pending")).Load()).HasPending, "second restart does not repeat catch-up bubble");
}
finally
{
    // The exact unique test root was created above, never derived from a user save.
    string resolved = Path.GetFullPath(temporaryRoot);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(resolved).StartsWith("EagleReminderSelfTest-", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing to remove an unexpected test root.");
    Directory.Delete(resolved, recursive: true);
}
Console.WriteLine($"Reminder self-test passed: {passed} checks.");
