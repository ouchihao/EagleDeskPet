using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DuckDeskPet.Core;

namespace DuckDeskPet;

/// <summary>An independently hosted local wall with no dependency on PetWindow, its renderer or pet progress</summary>
public partial class NotebookWindow : Window
{
    private readonly NotebookController _notebook;
    private readonly bool? _motionEnabled;
    private readonly ObservableCollection<NotebookRow> _rows = new();
    private readonly CancellationTokenSource _lifetime = new();
    private string _draftId = Guid.NewGuid().ToString("N");
    private string? _dialogId;
    private bool _deleteDialog, _ready, _closed, _animationBusy;
    private IInputElement? _returnFocus;
    private string? _feedback;

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(NotebookWindow), new PropertyMetadata(230d));
    public double CardWidth { get => (double)GetValue(CardWidthProperty); set => SetValue(CardWidthProperty, value); }
    public static readonly DependencyProperty CardHeightProperty = DependencyProperty.Register(
        nameof(CardHeight), typeof(double), typeof(NotebookWindow), new PropertyMetadata(249d));
    public double CardHeight { get => (double)GetValue(CardHeightProperty); set => SetValue(CardHeightProperty, value); }
    internal Task PendingAnimation { get; private set; } = Task.CompletedTask;

    public NotebookWindow(string dataDirectory) : this(new NotebookController(new NotebookStore(dataDirectory))) { }

    internal NotebookWindow(NotebookController notebook, bool? motionEnabled = null)
    {
        _notebook = notebook;
        _motionEnabled = motionEnabled;
        InitializeComponent();
        NotesItems.ItemsSource = _rows;
        _ready = true;
        Refresh();
    }

    private void Refresh()
    {
        if (!_ready || _closed) return;
        bool archived = ArchiveTab.IsChecked == true;
        var notes = _notebook.State.Notes.Where(x => x.IsCompleted == archived);
        notes = archived ? notes.OrderByDescending(x => x.CompletedUtc).ThenBy(x => x.Id)
            : notes.OrderByDescending(x => x.CreatedUtc).ThenBy(x => x.Id);
        _rows.Clear();
        foreach (var note in notes) _rows.Add(new NotebookRow(note, _notebook.CanSave));
        ActiveTab.Content = $"便签墙 · {_notebook.State.Notes.Count(x => !x.IsCompleted)}";
        ArchiveTab.Content = $"已完成 · {_notebook.State.Notes.Count(x => x.IsCompleted)}";
        CountText.Text = $"{_notebook.State.Notes.Count} / {NotebookBoard.MaximumNotes} 张";
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = !_notebook.CanSave ? "先把旧便签保管好" : archived ? "搞定的小事，收在这里" : "今天先搞定一件小事";
        EmptyBody.Text = !_notebook.CanSave ? "文件暂时不可读，请看下方提示。\n不会把旧内容当成空白覆盖。" : archived
            ? "墙上的便签点「完成」后会来到这里。\n随时可以恢复，不着急永久告别。"
            : "在上面写一句，替脑袋腾点位置。\n不催你打卡，也不给工作加班。";
        StatusText.Text = _notebook.Warning ?? _feedback ?? "完成是收好，不是删除；归档里的便签随时可以恢复。";
        RefreshComposer();
    }

    private void RefreshComposer()
    {
        if (!_ready) return;
        DraftCountText.Text = $"{ComposerBox.Text.Length} / {NotebookBoard.MaximumTextLength} 字符";
        AddButton.IsEnabled = !_animationBusy && _notebook.CanSave &&
            _notebook.State.Notes.Count < NotebookBoard.MaximumNotes && !string.IsNullOrWhiteSpace(ComposerBox.Text);
    }
    private void Composer_OnTextChanged(object sender, TextChangedEventArgs e) => RefreshComposer();

    private void Add_OnClick(object sender, RoutedEventArgs e) => AddDraft();
    private void AddDraft()
    {
        if (_closed || _animationBusy || DialogOverlay.Visibility == Visibility.Visible || string.IsNullOrWhiteSpace(ComposerBox.Text)) return;
        var result = _notebook.Add(_draftId, ComposerBox.Text);
        _feedback = result.Message;
        if (result.Success)
        {
            ComposerBox.Clear();
            _draftId = Guid.NewGuid().ToString("N");
            ActiveTab.IsChecked = true;
            WallScroller.ScrollToTop();
        }
        Refresh();
        ComposerBox.Focus();
        if (result.Changed && MotionEnabled) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_closed && NotesItems.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement card)
                PetUiMotion.Reveal(card, true);
        }));
    }

    private void Page_OnChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Refresh();
        WallScroller.ScrollToTop();
    }

    private void Wall_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Reserve the scrollbar even when absent: no wrap/un-wrap oscillation at the column boundary
        double available = Math.Max(270, e.NewSize.Width - 35);
        int columns = Math.Max(1, (int)Math.Floor(available / 235));
        CardWidth = Math.Min(360, available / columns - 14);
        CardHeight = Math.Clamp(e.NewSize.Height - 45, 176, 249);
    }

    private void Edit_OnClick(object sender, RoutedEventArgs e)
    {
        if (_closed || _animationBusy || (sender as Button)?.Tag is not string id) return;
        var note = _notebook.State.Notes.FirstOrDefault(x => x.Id == id);
        if (note is null) { Refresh(); return; }
        OpenDialog(id, delete: false);
        DialogTitle.Text = "展开这张便签";
        DialogBody.Text = "可以慢慢读，也可以改一改。Ctrl+Enter 保存；Enter 换行。";
        EditorBox.Text = note.Text;
        EditorBox.Visibility = Visibility.Visible;
        EditorBox.IsReadOnly = !_notebook.CanSave;
        SaveDialogButton.Content = "保存修改";
        SaveDialogButton.IsEnabled = _notebook.CanSave;
        FocusLater(EditorBox);
    }

    private void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (_closed || _animationBusy || (sender as Button)?.Tag is not string id || !_notebook.CanSave) return;
        var note = _notebook.State.Notes.FirstOrDefault(x => x.Id == id);
        if (note is null) return;
        OpenDialog(id, delete: true);
        DialogTitle.Text = "要永久删除这张便签吗？";
        DialogBody.Text = $"“{NotebookRow.ShortText(note.Text, 110)}”\n\n删除后无法从「已完成」恢复。如果只是做完了，请取消并选择「完成」。";
        EditorBox.Visibility = Visibility.Collapsed;
        SaveDialogButton.Content = "确认永久删除";
        SaveDialogButton.IsEnabled = true;
        // Safe default: Enter or accidental focus never silently destroys a note
        FocusLater(CancelDialogButton);
    }

    private void OpenDialog(string id, bool delete)
    {
        if (_animationBusy || _closed) return;
        _returnFocus = Keyboard.FocusedElement;
        _dialogId = id;
        _deleteDialog = delete;
        MainSurface.IsEnabled = false;
        DialogStatus.Text = _notebook.Warning ?? "";
        DialogOverlay.Visibility = Visibility.Visible;
    }

    private void SaveDialog_OnClick(object sender, RoutedEventArgs e) => SaveDialog();
    private void SaveDialog()
    {
        if (_dialogId is null || _closed || !SaveDialogButton.IsEnabled) return;
        var result = _deleteDialog ? _notebook.Delete(_dialogId) : _notebook.Edit(_dialogId, EditorBox.Text);
        if (!result.Success)
        {
            DialogStatus.Text = result.Message;
            SaveDialogButton.IsEnabled = _notebook.CanSave;
            return;
        }
        _feedback = result.Message;
        CloseDialog();
        Refresh();
        if (!_deleteDialog) ComposerBox.Focus();
        else (ArchiveTab.IsChecked == true ? ArchiveTab : ActiveTab).Focus();
    }

    private void CancelDialog_OnClick(object sender, RoutedEventArgs e) => CloseDialog();
    private void CloseDialog()
    {
        _dialogId = null;
        DialogOverlay.Visibility = Visibility.Collapsed;
        MainSurface.IsEnabled = !_animationBusy;
        if (_returnFocus is FrameworkElement { IsVisible: true } target) target.Focus();
        else (ArchiveTab.IsChecked == true ? ArchiveTab : ActiveTab).Focus();
    }

    private async void Toggle_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id || _animationBusy || _closed) return;
        var note = _notebook.State.Notes.FirstOrDefault(x => x.Id == id);
        if (note is null) return;
        if (note.IsCompleted)
        {
            var result = _notebook.Restore(id);
            _feedback = result.Message;
            Refresh();
            ArchiveTab.Focus();
            return;
        }
        PendingAnimation = CompleteAsync(id, FindCard((DependencyObject)sender));
        await PendingAnimation;
    }

    private async Task CompleteAsync(string id, FrameworkElement? card)
    {
        var result = _notebook.Complete(id);
        _feedback = result.Message;
        // Persistence and controller publication have already succeeded before any opacity changes
        if (!result.Changed || !MotionEnabled || card is null)
        { Refresh(); ActiveTab.Focus(); return; }
        _animationBusy = true;
        MainSurface.IsEnabled = false;
        var transform = new TranslateTransform();
        Transform original = card.RenderTransform;
        try
        {
            card.RenderTransform = transform;
            var duration = TimeSpan.FromMilliseconds(190);
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 19, duration)
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration));
            await Task.Delay(duration, _lifetime.Token);
        }
        catch (OperationCanceledException) { /* Closing does not undo a committed completion */ }
        finally
        {
            card.BeginAnimation(OpacityProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            card.RenderTransform = original;
            _animationBusy = false;
            if (!_closed) { MainSurface.IsEnabled = true; Refresh(); ActiveTab.Focus(); }
        }
    }

    private bool MotionEnabled => _motionEnabled ?? PetUiMotion.IsMotionEnabled;
    private static FrameworkElement? FindCard(DependencyObject child)
    {
        for (DependencyObject? current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { Name: "PaperCard" } element) return element;
        return null;
    }
    private void FocusLater(FrameworkElement element) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
    { if (!_closed && DialogOverlay.IsVisible) { element.Focus(); Keyboard.Focus(element); } }));

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DialogOverlay.Visibility == Visibility.Visible) CloseDialog(); else Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl+Enter is deliberately not a permanent-delete shortcut
            if (DialogOverlay.Visibility == Visibility.Visible) { if (!_deleteDialog) SaveDialog(); }
            else AddDraft();
            e.Handled = true;
        }
        else if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && DialogOverlay.Visibility != Visibility.Visible)
        { ComposerBox.Focus(); e.Handled = true; }
    }

    private void Window_OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private sealed class NotebookRow
    {
        public string Id { get; }
        public string Preview { get; }
        public string Stamp { get; }
        public string ActionLabel { get; }
        public bool CanWrite { get; }
        public Brush PaperBrush { get; }
        public NotebookRow(NotebookNote note, bool canWrite)
        {
            Id = note.Id;
            Preview = ShortText(note.Text, 180);
            CanWrite = canWrite;
            Stamp = (note.IsCompleted ? "DONE / 收好的小事 · " : "TO DO / 留给自己 · ") +
                (note.CompletedUtc ?? note.CreatedUtc).ToLocalTime().ToString("MM.dd", CultureInfo.InvariantCulture);
            ActionLabel = note.IsCompleted ? "↩ 恢复到墙上" : "✓ 完成";
            string[] colors = ["#FFF0B9", "#E3ECD2", "#F8DDCC", "#E0EAE9"];
            var start = (Color)ColorConverter.ConvertFromString(colors[Convert.ToInt32(note.Id[..2], 16) % colors.Length]);
            var brush = new LinearGradientBrush(start, Color.FromRgb((byte)(start.R * .97), (byte)(start.G * .96), (byte)(start.B * .95)), 90);
            brush.Freeze(); PaperBrush = brush;
        }
        internal static string ShortText(string text, int max)
        {
            if (text.Length <= max) return text;
            if (char.IsHighSurrogate(text[max - 1])) max--;
            return text[..max] + "…";
        }
    }
}
