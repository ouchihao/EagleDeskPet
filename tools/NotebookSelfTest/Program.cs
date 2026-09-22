using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DuckDeskPet;
using DuckDeskPet.Core;

internal static class Program
{
    private static int _checks;
    private static string _output = "";
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);
    private static string Id() => Guid.NewGuid().ToString("N");
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        _checks++; Console.WriteLine("PASS " + message);
    }
    private static void Reject(Action body, string message)
    {
        bool rejected = false;
        try { body(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException) { rejected = true; }
        Check(rejected, message);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Supply an isolated screenshot output directory."); return 2; }
        _output = Path.GetFullPath(args[0]);
        Console.WriteLine("SCREENSHOT OUTPUT " + _output);
        Directory.CreateDirectory(_output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
        try
        {
            CoreRules(); StoreRules(); ControllerRules(); WindowRules();
            Console.WriteLine($"Notebook self-test: {_checks} checks passed. Only isolated newly generated data directories were read/written.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { application.Shutdown(); }
    }

    private static string Json(NotebookSnapshot state) => JsonSerializer.Serialize(state);
    private static void CoreRules()
    {
        var blank = new NotebookSnapshot();
        Check(blank.Notes.Count == 0, "new notebook is empty");
        string id = Id();
        var added = NotebookBoard.Add(blank, id, "  今天喝水\r\n下班准时走 🦅  ", Now);
        Check(added.Changed && added.Snapshot.Notes.Single().Text == "今天喝水\n下班准时走 🦅", "multiline Unicode text normalized without losing emoji");
        Check(blank.Notes.Count == 0, "candidate mutation leaves original snapshot unchanged");
        NotebookBoard.Validate(added.Snapshot);
        Check(!NotebookBoard.Add(added.Snapshot, id, added.Snapshot.Notes[0].Text, Now.AddMinutes(1)).Changed, "same submission ID cannot add twice");
        Reject(() => NotebookBoard.Add(added.Snapshot, id, "different", Now), "duplicate ID with different content rejected");
        var two = NotebookBoard.Add(added.Snapshot, Id(), added.Snapshot.Notes[0].Text, Now).Snapshot;
        Check(two.Notes.Count == 2, "intentional identical text with separate ID is allowed");
        var edited = NotebookBoard.Edit(two, id, "改好：别忘了饭盒", Now.AddMinutes(2)).Snapshot;
        Check(edited.Notes.Count == 2 && edited.Notes[0].Id == id && edited.Notes[1] == two.Notes[1], "edit preserves identity and unrelated note");
        var done = NotebookBoard.SetCompleted(edited, id, true, Now.AddMinutes(3));
        Check(done.Changed && done.Snapshot.Notes.Count == 2 && done.Snapshot.Notes[0].CompletedUtc == Now.AddMinutes(3), "complete archives without deleting");
        Check(!NotebookBoard.SetCompleted(done.Snapshot, id, true, Now.AddDays(1)).Changed, "double complete is idempotent");
        var restored = NotebookBoard.SetCompleted(done.Snapshot, id, false, Now.AddMinutes(4));
        Check(restored.Changed && !restored.Snapshot.Notes[0].IsCompleted && restored.Snapshot.Notes[0].Text == edited.Notes[0].Text, "restore retains text and original ID");
        Check(!NotebookBoard.SetCompleted(restored.Snapshot, id, false, Now).Changed, "double restore is idempotent");
        var backwards = NotebookBoard.Edit(restored.Snapshot, id, "回拨时钟也不倒退日期", Now.AddDays(-1));
        Check(backwards.Snapshot.Notes[0].UpdatedUtc == restored.Snapshot.Notes[0].UpdatedUtc, "clock rollback does not break date invariants");
        var deleted = NotebookBoard.Delete(done.Snapshot, id);
        Check(deleted.Changed && deleted.Snapshot.Notes.Count == 1 && deleted.Snapshot.Notes[0].Id != id, "delete removes only explicit stable ID");
        Check(!NotebookBoard.Delete(deleted.Snapshot, id).Changed, "repeat delete does not remove next note");
        Reject(() => NotebookBoard.Edit(deleted.Snapshot, id, "不能复活", Now), "stale edit cannot recreate a deleted note");
        Reject(() => NotebookBoard.SetCompleted(deleted.Snapshot, id, true, Now), "stale completion cannot recreate deleted note");
        foreach (string text in new[] { "", " \n ", new string('中', 1001), "坏\0字", "\uD800" })
            Reject(() => NotebookBoard.Add(blank, Id(), text, Now), "reject empty, oversized or invalid Unicode/control input");
        Check(NotebookBoard.Add(blank, Id(), new string('中', 1000), Now).Changed, "1000-character boundary accepted");
        Reject(() => NotebookBoard.Add(blank, "not-an-id", "a", Now), "malformed note ID rejected");
        Reject(() => NotebookBoard.Validate(new() { Version = 2 }), "future save version rejected");
        Reject(() => NotebookBoard.Validate(new() { Notes = new[] { two.Notes[0], two.Notes[0] } }), "duplicate stored IDs rejected");
        Reject(() => NotebookBoard.Validate(new() { Notes = new[] { two.Notes[0] with { UpdatedUtc = Now.AddMinutes(-1) } } }), "stored date order validated");
        Reject(() => NotebookBoard.Validate(new() { Notes = new[] { two.Notes[0] with { CompletedUtc = Now.AddDays(1) } } }), "invalid completion date rejected");
        Reject(() => NotebookBoard.Validate(new() { Notes = new[] { two.Notes[0] with { CreatedUtc = Now.ToOffset(TimeSpan.FromHours(8)) } } }), "stored dates must be UTC");
        var many = new NotebookSnapshot();
        for (int i = 0; i < NotebookBoard.MaximumNotes; i++) many = NotebookBoard.Add(many, Id(), "便签 " + i, Now).Snapshot;
        many = NotebookBoard.SetCompleted(many, many.Notes[0].Id, true, Now).Snapshot;
        Reject(() => NotebookBoard.Add(many, Id(), "满了", Now), "256-note limit includes archive and only blocks additions");
        Check(NotebookBoard.Edit(many, many.Notes[1].Id, "满了还可以编辑", Now).Changed &&
            NotebookBoard.SetCompleted(many, many.Notes[0].Id, false, Now).Changed && NotebookBoard.Delete(many, many.Notes[2].Id).Changed,
            "full notebook still permits editing, restoring and deleting");
        Reject(() => NotebookBoard.Validate(new() { Notes = many.Notes.Concat(new[] { new NotebookNote(Id(), "more", Now, Now) }).ToArray() }), "oversized imported snapshot is rejected, never silently truncated");
        var mutable = two.Notes.ToList(); var copy = NotebookBoard.Copy(new() { Notes = mutable }); mutable.Clear();
        Check(copy.Notes.Count == 2 && copy.Notes is not List<NotebookNote>, "controller snapshot is detached and read-only");
    }

    private static void StoreRules()
    {
        using var f = new Fixture();
        string directory = Path.Combine(f.Root, "saved");
        var store = new NotebookStore(directory);
        Check(store.Load().Notes.Count == 0 && !Directory.Exists(directory), "empty notebook read creates no files");
        var state = NotebookBoard.Add(new(), Id(), "这句话需要保存 🦅", Now).Snapshot;
        Check(store.Save(state), "atomic initial save succeeds");
        string path = Path.Combine(directory, "notebook.json");
        var stale = new NotebookStore(directory);
        Check(Json(stale.Load()) == Json(state), "notebook reload preserves full Unicode state");
        var completed = NotebookBoard.SetCompleted(state, state.Notes[0].Id, true, Now.AddMinutes(1)).Snapshot;
        Check(store.Save(completed) && File.Exists(path + ".bak"), "replacement creates recoverable prior-file backup");
        Check(Json(new NotebookStore(directory).Load()) == Json(completed), "archive completion survives restart");
        string saved = File.ReadAllText(path);
        Check(!stale.Save(state) && !stale.CanSave && File.ReadAllText(path) == saved, "stale writer cannot overwrite newer notebook");
        Check(!new NotebookStore(directory).Save(state) && File.ReadAllText(path) == saved, "unobserved existing file cannot be overwritten");
        Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "atomic save leaves no temporary files");
        foreach (string contents in new[] { "{bad json", "{\"Version\":200,\"Notes\":[]}", "{\"Version\":1,\"Notes\":null}", new string('x', 2 * 1024 * 1024 + 1) })
        {
            string corruptDir = Path.Combine(f.Root, Id()); Directory.CreateDirectory(corruptDir);
            string corruptPath = Path.Combine(corruptDir, "notebook.json"); File.WriteAllText(corruptPath, contents);
            var corrupt = new NotebookStore(corruptDir);
            Check(corrupt.Load().Notes.Count == 0 && !corrupt.CanSave && corrupt.Warning is not null &&
                !corrupt.Save(new()) && File.ReadAllText(corruptPath) == contents, "corrupt/future/oversized file retained read-only rather than overwritten");
        }
        string blocked = Path.Combine(f.Root, "a-file-not-directory"); File.WriteAllText(blocked, "fixture");
        var failed = new NotebookStore(blocked); failed.Load();
        Check(!failed.Save(state) && failed.Warning is not null, "I/O failure is visible and never reports success");
        var locked = new NotebookStore(directory); locked.Load();
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(!locked.Save(state), "locked save file cannot acknowledge a mutation");
        Check(File.ReadAllText(path) == saved && locked.Save(state), "transient write failure can retry after lock release");
        Check(Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories).All(x => !x.EndsWith("pet-state.json") && !x.EndsWith("reminders.json")), "notebook never creates pet or reminder data");
    }

    private static void ControllerRules()
    {
        var memory = new MemoryStore(); var controller = new NotebookController(memory);
        string id = Id();
        Check(controller.Add(id, "第一张").Changed && memory.Saves == 1, "controller persists new note once");
        Check(!controller.Add(id, "第一张").Changed && memory.Saves == 1, "double submit does not save a second note");
        string before = Json(controller.State);
        memory.Fail = true;
        Check(!controller.Complete(id).Success && Json(controller.State) == before, "failed completion leaves live note on wall");
        Check(!controller.Edit(id, "错误覆盖").Success && Json(controller.State) == before, "failed edit preserves existing content");
        Check(!controller.Delete(id).Success && Json(controller.State) == before, "failed deletion preserves existing content");
        memory.Fail = false;
        bool committedBeforePublish = false;
        memory.BeforeCommit = _ => committedBeforePublish = !controller.State.Notes[0].IsCompleted;
        Check(controller.Complete(id).Changed && committedBeforePublish && controller.State.Notes[0].IsCompleted, "durable save happens before controller publishes completion");
        memory.BeforeCommit = null;
        var restarted = new NotebookController(memory);
        Check(restarted.State.Notes[0].IsCompleted && restarted.Restore(id).Changed, "new controller restores archived note without changing ID");
        Check(!restarted.Add(Id(), " ").Success, "invalid composer input produces friendly result");
    }

    private static void WindowRules()
    {
        var memory = new MemoryStore(); var controller = new NotebookController(memory);
        var window = new NotebookWindow(controller, motionEnabled: false);
        try
        {
            Layout(window, 836, 774);
            Check(((Border)window.FindName("EmptyState")).Visibility == Visibility.Visible, "first-open empty wall is designed and visible");
            Save(window, "notebook-empty.png");
            var composer = (TextBox)window.FindName("ComposerBox");
            Check(composer.AcceptsReturn && !composer.AcceptsTab && composer.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && composer.MaxLength == 1000,
                "composer supports newline and scrolling while Tab moves focus");
            composer.Text = "记得带饭盒，顺便给生活留一点空白。"; Click(window, "AddButton");
            string first = controller.State.Notes.Single().Id;
            composer.Text = "把重构计划拆成可验收的小步骤\n今天只完成最重要的一步。"; Click(window, "AddButton");
            Check(controller.State.Notes.Count == 2 && composer.Text == "", "two UI additions create two stable notes and clear successful draft");
            Click(window, "AddButton");
            Check(controller.State.Notes.Count == 2, "double-click after cleared draft cannot duplicate submission");
            Layout(window, 836, 774);
            Check(((ItemsControl)window.FindName("NotesItems")).Items.Count == 2 && Descendants<Grid>((DependencyObject)window.Content).Count(x => x.Name == "PaperCard") == 2, "wall renders one real paper card per note");
            Check(Descendants<System.Windows.Shapes.Ellipse>((DependencyObject)window.Content).Count() >= 5, "paper cards contain visible tack geometry, not just text rows");
            Save(window, "notebook-two-notes.png");
            CardButton(window, first, "展开 / 编辑").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((Grid)window.FindName("DialogOverlay")).Visibility == Visibility.Visible && !((Grid)window.FindName("MainSurface")).IsEnabled, "editor overlay disables background interaction");
            string longText = string.Concat(Enumerable.Repeat("别着急，这张便签支持长内容、中文与 emoji 🦅。\n", 25));
            var editor = (TextBox)window.FindName("EditorBox"); editor.Text = longText;
            Check(editor.AcceptsReturn && !editor.AcceptsTab && editor.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                "editor supports newline and scrolling while Tab moves focus");
            Save(window, "notebook-editor.png");
            Click(window, "SaveDialogButton");
            Check(controller.State.Notes.Count == 2 && controller.State.Notes.Single(x => x.Id == first).Text == longText.Trim(), "UI edit changes same note, does not add another");
            Layout(window, 460, 620);
            Check(Descendants<Grid>((DependencyObject)window.Content).Where(x => x.Name == "PaperCard").All(x => x.ActualWidth <= 390), "narrow wall adapts cards without horizontal overflow");
            Check(((ScrollViewer)window.FindName("WallScroller")).HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled &&
                ((ScrollViewer)window.FindName("WallScroller")).ExtentHeight > ((ScrollViewer)window.FindName("WallScroller")).ViewportHeight,
                "long narrow wall scrolls vertically rather than hiding notes");
            Save(window, "notebook-narrow.png");
            var scroller = (ScrollViewer)window.FindName("WallScroller");
            var firstComplete = Descendants<Button>((DependencyObject)window.Content).First(x => (string?)x.Content == "✓ 完成");
            Point completePosition = firstComplete.TranslatePoint(new Point(0, firstComplete.ActualHeight), scroller);
            Check(completePosition.Y <= scroller.ActualHeight, "first card completion stays visible at minimum content height");
            CardButton(window, first, "展开 / 编辑").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Save(window, "notebook-editor-narrow.png");
            var dialogCard = (Border)window.FindName("DialogCard");
            var dialogSave = (Button)window.FindName("SaveDialogButton");
            Check(dialogCard.ActualWidth <= 416 && dialogSave.TranslatePoint(new Point(0, dialogSave.ActualHeight), (FrameworkElement)window.Content).Y <= 620,
                "narrow editor keeps confirmation buttons inside visible client area");
            Click(window, "CancelDialogButton");
            CardButton(window, first, "✓ 完成").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.PendingAnimation.IsCompleted && controller.State.Notes.Single(x => x.Id == first).IsCompleted &&
                ((ItemsControl)window.FindName("NotesItems")).Items.Count == 1, "reduced motion completes immediately only after persistence");
            ((RadioButton)window.FindName("ArchiveTab")).IsChecked = true; Layout(window, 836, 774);
            Check(((ItemsControl)window.FindName("NotesItems")).Items.Count == 1, "completed note is accessible in separate archive");
            Save(window, "notebook-archive.png");
            CardButton(window, first, "↩ 恢复到墙上").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!controller.State.Notes.Single(x => x.Id == first).IsCompleted, "UI restore changes only completion state");
            ((RadioButton)window.FindName("ActiveTab")).IsChecked = true; Layout(window, 836, 774);
            CardButton(window, first, "删除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(controller.State.Notes.Count == 2 && ((Button)window.FindName("SaveDialogButton")).Content.ToString() == "确认永久删除", "delete opens explicit confirmation without deleting");
            Save(window, "notebook-delete-confirm.png");
            Click(window, "CancelDialogButton");
            Check(controller.State.Notes.Count == 2, "cancel deletion preserves note");
            CardButton(window, first, "删除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Click(window, "SaveDialogButton");
            Check(controller.State.Notes.Count == 1 && controller.State.Notes[0].Id != first, "confirmed permanent deletion targets only chosen ID");
            Layout(window, 836, 774);
            string remaining = controller.State.Notes[0].Id;
            memory.Fail = true;
            composer.Text = "写失败时不要弄丢我"; Click(window, "AddButton");
            Check(composer.Text == "写失败时不要弄丢我" && controller.State.Notes.Count == 1, "failed add retains editable draft");
            CardButton(window, remaining, "✓ 完成").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!controller.State.Notes[0].IsCompleted && ((ItemsControl)window.FindName("NotesItems")).Items.Count == 1, "failed save never animates a note away");
            Check(((TextBlock)window.FindName("StatusText")).Text.Contains("失败"), "save error is visibly announced");
            memory.Fail = false;
            CardButton(window, remaining, "展开 / 编辑").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(KeyboardNavigation.GetTabNavigation((Grid)window.FindName("DialogOverlay")) == KeyboardNavigationMode.Cycle &&
                ((Button)window.FindName("CancelDialogButton")).Focusable && ((Button)window.FindName("SaveDialogButton")).Focusable,
                "dialog provides keyboard focus cycle and native focusable controls");
            SendEscape(window);
            Check(((Grid)window.FindName("DialogOverlay")).Visibility == Visibility.Collapsed, "Escape closes dialog rather than losing the wall");
            Layout(window, 460, 620);
            // Render at two device scales; this verifies rasterization/layout, not a physical monitor DPI move
            Save(window, "notebook-narrow-150.png", 1.5);
            Save(window, "notebook-narrow-200.png", 2);
        }
        finally { window.Close(); }

        var animatedStore = new MemoryStore(); var animatedController = new NotebookController(animatedStore);
        string animatedId = Id(); animatedController.Add(animatedId, "先保存，再收起来");
        var animated = new NotebookWindow(animatedController, motionEnabled: true);
        try
        {
            Layout(animated, 836, 774);
            CardButton(animated, animatedId, "✓ 完成").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(animatedStore.State.Notes[0].IsCompleted && !animated.PendingAnimation.IsCompleted &&
                ((ItemsControl)animated.FindName("NotesItems")).Items.Count == 1, "completion is durable while original card is still animating");
            WaitUi(animated.PendingAnimation);
            Check(((ItemsControl)animated.FindName("NotesItems")).Items.Count == 0, "card disappears after finite removal animation");
        }
        finally { animated.Close(); }

        var closingStore = new MemoryStore(); var closingController = new NotebookController(closingStore);
        string closingId = Id(); closingController.Add(closingId, "关闭中也已经存好了");
        var closing = new NotebookWindow(closingController, motionEnabled: true); Layout(closing, 836, 774);
        CardButton(closing, closingId, "✓ 完成").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); closing.Close();
        WaitUi(closing.PendingAnimation);
        Check(new NotebookController(closingStore).State.Notes[0].IsCompleted, "closing during animation cancels visual work without undoing committed archive");

        var galleryStore = new MemoryStore(); var galleryController = new NotebookController(galleryStore);
        string[] examples = ["把重构计划拆成小步骤\n今天先验证透明窗口。", "午休去晒五分钟太阳\n电脑不会替我补充维生素。", "周五记得给同事演示便签墙", "检查 PR 里的反馈\n看完一条，认真回复一条。", "下班前把明天最重要的事写下来", "记得带饭盒！\n饭盒没有长腿，不会自己回家。"];
        for (int i = 0; i < examples.Length; i++) galleryController.Add((16 + i).ToString("x2") + Guid.NewGuid().ToString("N")[2..], examples[i]);
        var gallery = new NotebookWindow(galleryController, motionEnabled: false);
        try
        {
            Layout(gallery, 836, 860);
            Check(((ItemsControl)gallery.FindName("NotesItems")).Items.Count == 6, "multiple colored notes remain individually reachable on the paper wall");
            Save(gallery, "notebook-six-notes.png");
            Layout(gallery, 460, 582);
            var firstAction = Descendants<Button>((DependencyObject)gallery.Content).First(x => (string?)x.Content == "✓ 完成");
            var viewport = (ScrollViewer)gallery.FindName("WallScroller");
            Check(firstAction.TranslatePoint(new Point(0, firstAction.ActualHeight), viewport).Y <= viewport.ActualHeight,
                "minimum outer-window height allowance still exposes first card actions");
            Save(gallery, "notebook-small-client.png");
        }
        finally { gallery.Close(); }
    }

    private static void Click(NotebookWindow window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Button CardButton(NotebookWindow window, string id, string label)
    {
        var surface = (FrameworkElement)window.Content;
        Layout(window, surface.Width, surface.Height);
        return Descendants<Button>(surface).Single(x => (string?)x.Tag == id && (string?)x.Content == label);
    }
    private static void SendEscape(Window window)
    {
        var source = new TestPresentationSource();
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(key);
    }
    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Layout(Window window, double width, double height)
    {
        var content = (FrameworkElement)window.Content;
        content.Width = width; content.Height = height;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        Pump(); content.UpdateLayout();
    }
    private static void Save(Window window, string name, double scale = 1)
    {
        var surface = (FrameworkElement)window.Content;
        Layout(window, surface.Width, surface.Height);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth * scale), (int)Math.Ceiling(surface.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(surface); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_output, name)); png.Save(stream);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void WaitUi(Task task)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted) { Pump(); if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Animation did not settle."); }
        task.GetAwaiter().GetResult();
    }
    private sealed class MemoryStore : INotebookPersistence
    {
        internal NotebookSnapshot State = new();
        internal bool Fail;
        internal int Saves;
        internal Action<NotebookSnapshot>? BeforeCommit;
        public string? Warning => Fail ? "测试保存失败；本次操作未生效。" : null;
        public bool CanSave => true;
        public NotebookSnapshot Load() => NotebookBoard.Copy(State);
        public bool Save(NotebookSnapshot state)
        {
            if (Fail) return false;
            BeforeCommit?.Invoke(state);
            State = NotebookBoard.Copy(state); Saves++; return true;
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "EagleNotebookSelfTest-" + Guid.NewGuid().ToString("N"));
        internal Fixture() { Console.WriteLine("ISOLATED DATA " + Root); Directory.CreateDirectory(Root); }
        public void Dispose()
        {
            string full = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(full).StartsWith("EagleNotebookSelfTest-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test directory; refusing deletion.");
            Directory.Delete(full, recursive: true);
        }
    }
}
