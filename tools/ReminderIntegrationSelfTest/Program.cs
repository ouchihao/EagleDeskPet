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
    [STAThread]
    private static void Main(string[] args)
    {
        int checks = 0;
        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException(message);
            checks++; Console.WriteLine("PASS " + message);
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string root = Path.Combine(Path.GetTempPath(), "EagleReminderIntegration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("ISOLATED TEST DIRECTORY " + root);
        try
        {
            var now = DateTimeOffset.UtcNow;
            PetWindow Create(params (string Message, bool Repeat)[] reminders)
            {
                AppPaths.DataDirectory = Path.Combine(root, Guid.NewGuid().ToString("N"));
                var scheduler = new ReminderScheduler();
                foreach (var item in reminders) scheduler.Add(item.Message, 1, item.Repeat, now.AddHours(-2));
                var store = new ReminderStore(AppPaths.DataDirectory); store.Load();
                if (!store.Save(scheduler.Snapshot())) throw new InvalidOperationException(store.Warning);
                return new PetWindow();
            }
            var pet = Create(("喝水", false), ("起身", true));
            object last = pet.LastNotice;
            pet.Block(notice: true); pet.Tick(now);
            Check(pet.PendingReminder && pet.BubbleCount == 0 && pet.HasActiveNotice, "active AI notice remains untouched while reminders wait");
            pet.Block(queued: true); pet.Tick(now);
            Check(pet.BubbleCount == 0, "queued external notifications remain ahead of reminders");
            pet.Block(bubble: true); pet.Tick(now);
            Check(pet.BubbleCount == 0, "visible bubble is not overwritten");
            pet.Block(drag: true); pet.Tick(now);
            Check(pet.BubbleCount == 0, "drag defers local bubble without discarding it");
            pet.Block(assets: false); pet.Tick(now);
            Check(pet.BubbleCount == 0 && pet.PendingReminder, "startup assets defer rather than lose due reminders");
            pet.Block(); pet.GitHubPending = true; pet.Tick(now);
            Check(pet.BubbleCount == 0 && pet.HasActiveNotice, "GitHub announcement gets first chance on free bubble");
            pet.Block(); pet.TaskPending = true; pet.Tick(now);
            Check(pet.BubbleCount == 0 && pet.HasActiveNotice, "task announcement gets first chance on free bubble");
            pet.Block(); pet.Tick(now);
            Check(pet.BubbleCount == 1 && pet.LastBubble.Contains("2 件事") && !pet.PendingReminder, "one merged restart catch-up bubble");
            Check(ReferenceEquals(last, pet.LastNotice) && pet.ExternalUnread == 4 && !pet.HasActiveNotice, "local notification does not own last-notice or unread state");
            pet.Block(); pet.Tick(now.AddSeconds(1));
            Check(pet.BubbleCount == 1, "same due batch not repeated on following tick");
            pet.Block(); pet.Tick(now.AddMinutes(1));
            Check(pet.BubbleCount == 2 && pet.LastBubble.Contains("起身"), "repeat interval continues after catch-up");
            pet.StartForTest(); Check(pet.TimerRunning, "reminder timer starts independently");
            pet.StopForTest(); Check(!pet.TimerRunning, "closing stops reminder timer");

            AppPaths.DataDirectory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            var staggered = new ReminderScheduler();
            staggered.Add("第一件", 1, false, now.AddMinutes(-1));
            staggered.Add("第二件", 1, false, now.AddSeconds(-40));
            var staggeredStore = new ReminderStore(AppPaths.DataDirectory); staggeredStore.Load(); staggeredStore.Save(staggered.Snapshot());
            var throttled = new PetWindow();
            throttled.Tick(now); throttled.Block(); throttled.Tick(now.AddSeconds(20));
            Check(throttled.BubbleCount == 1 && throttled.PendingReminder, "different due batches respect the thirty-second minimum gap");
            throttled.Tick(now.AddSeconds(30));
            Check(throttled.BubbleCount == 2 && !throttled.PendingReminder, "throttled reminder remains durable and is shown later");

            var conflicted = Create(("不能重复弹", false));
            var otherStore = new ReminderStore(AppPaths.DataDirectory);
            var other = new ReminderScheduler(otherStore.Load()); other.Add("另一个实例", 1, false, now);
            otherStore.Save(other.Snapshot());
            conflicted.Tick(now);
            Check(conflicted.BubbleCount == 0 && conflicted.ReminderWarning is not null, "concurrent store modification blocks bubble and stale overwrite");

            var editor = Create();
            Check(editor.AddReminder("提醒内容", 0, false) is not null && editor.ReminderItems.Count == 0, "UI-facing invalid input returns error without mutation");
            Check(editor.AddReminder("提醒内容", 20, true) is null && editor.ReminderItems.Count == 1, "UI-facing creation persists");
            string id = editor.ReminderItems[0].Id;
            Check(editor.ToggleReminder(id) is null && editor.ReminderItems[0].IsPaused, "UI pause persists frozen state");
            Check(editor.DeleteReminder(id) is null && editor.ReminderItems.Count == 0, "UI deletion persists");

            ReminderNotificationServices.Current = new RejectedNativeChannel();
            string beforeEnable = File.ReadAllText(Path.Combine(AppPaths.DataDirectory, "reminders.json"));
            Check(editor.SetReminderChannels(true, false) is not null && !editor.NativeReminderEnabled && editor.ReminderBubbleEnabled,
                "native readiness failure does not save enabled state or alter bubble choice");
            Check(File.ReadAllText(Path.Combine(AppPaths.DataDirectory, "reminders.json")) == beforeEnable,
                "failed prepare leaves reminder file byte-identical");

            if (args.Length == 1)
            {
                string output = Path.GetFullPath(args[0]);
                string allowed = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".codex-build")) + Path.DirectorySeparatorChar;
                if (!output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("UI evidence must stay within this repository's .codex-build.");
                Console.WriteLine("EXACT UI EVIDENCE TARGET " + output);
                Directory.CreateDirectory(output);
                var panel = new ReminderWindow(editor);
                Render(panel, 550, 690, Path.Combine(output, "reminders-empty.png"));
                for (int i = 0; i < 8; i++) editor.AddReminder(i == 0 ? "喝口水吧，别光给工作续杯。" : i == 1 ? "起来活动一下，椅子不会替你升职。" : "这是第 " + (i + 1) + " 件只保存在本机的小事。", 20 + i * 5, i % 2 == 0);
                editor.ToggleReminder(editor.ReminderItems[1].Id);
                panel.Refresh(DateTimeOffset.UtcNow);
                Render(panel, 550, 690, Path.Combine(output, "reminders-full.png"));
                Render(panel, 460, 600, Path.Combine(output, "reminders-compact.png"));
                Check(((ListBox)panel.FindName("ReminderList")).Items.Count == 8 && !((Button)panel.FindName("AddButton")).IsEnabled, "actual WPF list shows bounded eight items and disables add");
                Check(((TextBox)panel.FindName("MessageBox")).MaxLength == 120, "actual WPF editor enforces message length");
                panel.Close();
            }
            Check(!Directory.EnumerateFiles(root, "pet-state.json", SearchOption.AllDirectories).Any(), "reminder integration never writes pet economy saves");
            Console.WriteLine($"Reminder integration self-test passed: {checks} checks.");
        }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("EagleReminderIntegration-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove an unexpected test root.");
            Directory.Delete(resolved, recursive: true);
            app.Shutdown();
        }
    }

    private static void Render(Window window, double width, double height, string path)
    {
        // Render our own measured visual tree without showing a native window.
        var element = (FrameworkElement)window.Content;
        element.Width = width; element.Height = height;
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        var image = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        image.Render(background);
        image.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private sealed class RejectedNativeChannel : IReminderNativeChannel
    {
        public ReminderNativeResult Prepare() => new(false, "injected native registration failure");
        public ReminderNativeResult Send(IReadOnlyList<LocalReminder> batch) => throw new InvalidOperationException("must not send");
        public ReminderNativeResult SendTest() => new(false, "injected test failure");
    }
}
