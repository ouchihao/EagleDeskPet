using System.Windows;
using DuckDeskPet.Core;

namespace DuckDeskPet;

internal static class AppPaths
{
    public static string DataDirectory { get; set; } = "";
}

public partial class PetWindow : Window
{
    private bool _isClosing = false;
    private bool _assetsReady = true;
    private bool _nativeDragInProgress;
    private object? _activeNotice;
    private readonly object _lastNotice = new();
    private readonly Queue<object> _notices = new();
    private FakeBubble _bubble = new();
    public int BubbleCount { get; private set; }
    public string LastBubble { get; private set; } = "";
    public bool GitHubPending { get; set; }
    public bool TaskPending { get; set; }
    public int ExternalUnread { get; private set; } = 4;
    internal object LastNotice => _lastNotice;
    internal bool HasActiveNotice => _activeNotice is not null;
    internal bool PendingReminder => _reminders.HasPending;
    internal bool TimerRunning => _reminderTimer.IsEnabled;
    public PetWindow() => InitializeReminders();
    public void Tick(DateTimeOffset now) => TickReminders(now);
    public void StartForTest() => StartReminders();
    public void StopForTest() => StopReminders();
    public void Block(bool bubble = false, bool notice = false, bool queued = false, bool drag = false, bool assets = true)
    {
        _bubble.IsVisible = bubble;
        _activeNotice = notice ? new object() : null;
        _notices.Clear(); if (queued) _notices.Enqueue(new());
        _nativeDragInProgress = drag; _assetsReady = assets;
    }
    private void TryShowGitHubNotice()
    {
        if (!GitHubPending || _activeNotice is not null || _bubble.IsVisible) return;
        _activeNotice = new(); _bubble.IsVisible = true; GitHubPending = false;
    }
    private void TickTaskNotifications()
    {
        if (!TaskPending || _activeNotice is not null || _bubble.IsVisible) return;
        _activeNotice = new(); _bubble.IsVisible = true; TaskPending = false;
    }
    private void ShowBubble(string source, string message, string quip, bool canOpen, double seconds, bool isBanter = false)
    {
        if (canOpen || source.Length > 0) throw new InvalidOperationException("Local reminder must not become an external notice.");
        BubbleCount++; LastBubble = message; _bubble.IsVisible = true;
    }
    private void PlaceCompanion(Window window, bool above) { }
    private sealed class FakeBubble { public bool IsVisible { get; set; } }
}
