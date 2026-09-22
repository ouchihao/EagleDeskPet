using DuckDeskPet;
using DuckDeskPet.Core;
using System.Text.Json;

int count = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); count++; }
var now = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
var scheduler = new ReminderScheduler();
Check(!scheduler.NativeNotificationsEnabled && scheduler.BubbleNotificationsEnabled, "legacy-compatible explicit opt-in defaults");
scheduler.SetChannels(true, true);
var once = scheduler.Add("喝水", 1, false, now);
var repeat = scheduler.Add("工作休息", 1, true, now);
scheduler.Advance(now.AddHours(3));
Check(scheduler.HasPendingNative && scheduler.HasPending, "independent channels become pending at due time");
var due = scheduler.Items.ToArray();
Check(due.Select(x => x.CycleId).Distinct().Count() == 2 && due.All(x => x.CycleId.Length == 32), "each reminder receives a stable due cycle");
var reserved = scheduler.ReserveNativeBatch();
Check(reserved.Count == 2 && !scheduler.HasPendingNative && scheduler.HasPending, "one coalesced native reservation does not consume bubble");
Check(scheduler.ReserveNativeBatch().Count == 0, "reservation cannot be submitted twice");
var interrupted = new ReminderScheduler(scheduler.Snapshot());
Check(interrupted.RecoverInterruptedDeliveries() && interrupted.Items.All(x => x.NativeDelivery == NativeReminderDelivery.Unknown), "interrupted reservation becomes visibly uncertain");
Check(!interrupted.HasPendingNative && interrupted.ReserveNativeBatch().Count == 0, "uncertain OS attempt never auto-replayed after restart");
scheduler.CompleteNativeBatch(reserved, true, "已提交，非已读");
Check(scheduler.Items.All(x => x.NativeDelivery == NativeReminderDelivery.Accepted), "accepted means API submission only");
scheduler.AcknowledgePending(now);
Check(scheduler.Items.All(x => x.NativeDelivery == NativeReminderDelivery.Accepted), "bubble acknowledgement preserves independent native ledger");
Check(scheduler.IsCurrentActivation(once.Id, due[0].CycleId), "current once-only notification can locate completed reminder");
scheduler.TogglePause(once.Id, now.AddHours(3));
Check(!scheduler.IsCurrentActivation(once.Id, due[0].CycleId), "restart invalidates old notification without changing new countdown");
scheduler.Advance(now.AddHours(3).AddMinutes(1));
Check(scheduler.Items.Single(x => x.Id == repeat.Id).CycleId != due[1].CycleId, "next repeat has a fresh dedupe cycle");
Check(!scheduler.IsCurrentActivation(repeat.Id, due[1].CycleId), "old repeat click is stale");
var next = scheduler.ReserveNativeBatch();
scheduler.CompleteNativeBatch(next, false, "系统关闭通知，待查看");
Check(scheduler.Items.All(x => x.NativeDelivery == NativeReminderDelivery.Failed) && !scheduler.HasPendingNative,
    "failure retained for viewing without uncontrolled retries");
Check(scheduler.Items.All(x => x.NativeDetail!.Contains("待查看")), "failure is not presented as success");
scheduler.TogglePause(repeat.Id, now.AddHours(3).AddMinutes(1));
Check(!scheduler.IsCurrentActivation(repeat.Id, next.Single(x => x.Id == repeat.Id).CycleId), "pause invalidates prior activation");
scheduler.Remove(once.Id);
Check(!scheduler.IsCurrentActivation(once.Id, next.Single(x => x.Id == once.Id).CycleId), "deleted ID cannot be revived by click");
var off = new ReminderScheduler(); off.Add("off", 1, false, now); off.Advance(now.AddDays(1)); off.SetChannels(true, false);
Check(!off.HasPending && !off.HasPendingNative, "newly enabled channel never replays old off-cycle");
var migrated = new ReminderScheduler(JsonSerializer.Deserialize<ReminderSnapshot>("{\"Version\":1,\"Items\":[]}"));
Check(migrated.Snapshot().Version == 2 && !migrated.NativeNotificationsEnabled, "version-one migration retains opt-in policy");
string good = new ReminderNativeActivation(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")).Argument;
Check(ReminderNativeActivation.Parse(good) is not null, "strict activation accepts ID and cycle");
foreach (var bad in new[] { "", "action=reminder&id=../x&cycle=1", good + "&url=https://evil", good + "\n", "action=delete", new string('x', 200) })
    Check(ReminderNativeActivation.Parse(bad) is null, "reject unsafe or unknown activation");
Check(ReminderNativeActivation.Parse("action=reminder-test")!.IsTest, "test activation has no business mutation");
int subscribes = 0, unsubscribes = 0;
var registration = new ReminderRegistrationGate();
try { registration.Ensure(() => subscribes++, () => throw new IOException("injected registration failure"), () => unsubscribes++); } catch (IOException) { }
Check(!registration.IsRegistered && subscribes == 1 && unsubscribes == 1, "failed registration rolls subscription back immediately");
registration.Ensure(() => subscribes++, () => { }, () => unsubscribes++);
registration.Ensure(() => subscribes++, () => { }, () => unsubscribes++);
Check(registration.IsRegistered && subscribes == 2 && unsubscribes == 1, "successful retry subscribes once and repeated enable is idempotent");
registration.Detach(() => unsubscribes++); registration.Detach(() => unsubscribes++);
Check(!registration.IsRegistered && unsubscribes == 2, "registration disposal is idempotent");
var uncertain = new ReminderScheduler(); uncertain.SetChannels(true, false); uncertain.Add("未知结果", 1, false, now); uncertain.Advance(now.AddMinutes(1));
var uncertainBatch = uncertain.ReserveNativeBatch(); uncertain.CompleteNativeBatch(uncertainBatch, false, "结果未知", uncertain: true);
var uncertainReload = new ReminderScheduler(uncertain.Snapshot());
Check(uncertainReload.Items[0].NativeDelivery == NativeReminderDelivery.Unknown && !uncertainReload.HasPendingNative &&
    uncertainReload.IsCurrentActivation(uncertainBatch[0].Id, uncertainBatch[0].CycleId), "unknown transport result persists without replay and can still locate current cycle");

string root = Path.Combine(Path.GetTempPath(), "EagleNativeReminderSelfTest-" + Guid.NewGuid().ToString("N"));
Console.WriteLine("ISOLATED TEST DIRECTORY " + root);
Directory.CreateDirectory(root);
try
{
    var store = new ReminderStore(root); store.Load();
    var persisted = new ReminderScheduler(); persisted.SetChannels(true, false); persisted.Add("原子投递", 1, true, now); persisted.Advance(now.AddDays(1));
    Check(store.Save(persisted.Snapshot()), "due event and ledger are one atomic file");
    var batch = persisted.ReserveNativeBatch(); Check(store.Save(persisted.Snapshot()), "reservation durable before native call");
    var reloadStore = new ReminderStore(root); var reload = new ReminderScheduler(reloadStore.Load());
    Check(reload.RecoverInterruptedDeliveries() && reloadStore.Save(reload.Snapshot()), "crash boundary recovers durable uncertainty");
    Check(!new ReminderScheduler(new ReminderStore(root).Load()).HasPendingNative, "second restart cannot resend same cycle");
    Check(!store.Save(persisted.Snapshot()), "stale writer cannot overwrite reservation ledger");
    string lateDirectory = Path.Combine(root, "late-failure");
    var lateStore = new ReminderStore(lateDirectory); lateStore.Load();
    var late = new ReminderScheduler(); late.SetChannels(true, false); late.Add("系统稍后失败", 1, true, now); late.Advance(now.AddMinutes(1));
    var sent = late.ReserveNativeBatch(); late.CompleteNativeBatch(sent, true, "已提交"); lateStore.Save(late.Snapshot());
    Check(late.RecordNativeFailure(sent, "系统异步报告失败") && lateStore.Save(late.Snapshot()), "asynchronous Windows failure persists after accepted submission");
    Check(new ReminderScheduler(new ReminderStore(lateDirectory).Load()).Items[0].NativeDelivery == NativeReminderDelivery.Failed,
        "late failure is retained across reload and not claimed as accepted");
    late.Advance(now.AddMinutes(2));
    Check(!late.RecordNativeFailure(sent, "旧回调") && late.Items[0].NativeDelivery == NativeReminderDelivery.Pending,
        "old cycle failure callback cannot overwrite a newer reminder cycle");
    Check(!Directory.EnumerateFiles(root).Any(p => p.Contains("pet-state")), "no economy or external account state touched");
}
finally
{
    string resolved = Path.GetFullPath(root);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(resolved).StartsWith("EagleNativeReminderSelfTest-", StringComparison.Ordinal)) throw new Exception("Unsafe test cleanup");
    Directory.Delete(resolved, true);
}
Console.WriteLine($"Native reminder core: {count} passed");
