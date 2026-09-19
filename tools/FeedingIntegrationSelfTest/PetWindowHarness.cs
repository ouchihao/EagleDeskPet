using System.Windows;
using System.Windows.Threading;
using DuckDeskPet.Core;

namespace DuckDeskPet;

// The production Feeding partial, care, controller and store are unchanged.
// This shell supplies the normal interaction guard and explicitly advances the
// controller as a fake renderer; full-host guards and real frames are GUI-smoke work.
public partial class PetWindow : Window
{
    private readonly PetBehaviorController _behavior = new();
    private readonly PetStore _store;
    private readonly PetCareService _care;
    private readonly CarePanel? _carePanel = null;
    private bool _assetsReady = true, _pendingFeed;
    internal readonly TestTime Time;
    internal PetState CareState => _care.State;
    internal bool WorkInProgress => CareState.IsWorking || _behavior.IsWorkSceneActive || _behavior.IsWorkRequested;
    internal bool InteractionsUnavailable => !_assetsReady || _behavior.IsPaused || WorkInProgress || IsFeeding;
    internal string CareStatus { get; private set; } = "";
    internal string Speech = "";
    internal bool WasClosed;
    internal string DirectoryPath { get; }
    internal PetBehaviorController Behavior => _behavior;
    internal bool PendingFeed => _pendingFeed;

    internal PetWindow(string directory)
    {
        DirectoryPath = directory;
        Time = new(); _feedingTime = Time;
        _store = new(directory);
        _care = new(_store.Load(), Time.GetUtcNow());
        CareState.Food = 10; CareState.Fullness = 20;
        _behavior.SuspendAutomatic(true);
        Width = Height = 10; Left = Top = -20000; ShowInTaskbar = false;
        Closing += (_, e) => { if (!TryCloseFeedingForPetExit()) e.Cancel = true; };
        Closed += (_, _) => { WasClosed = true; StopFeeding(); };
        Show();
    }
    private void Say(string text) => Speech = text;
    internal void RenderStep(double delta = 1.0 / 60)
    {
        _behavior.Advance(delta);
        TickFeeding();
    }
    internal void RenderUntil(Func<bool> condition, int maxFrames = 3000)
    {
        while (!condition() && maxFrames-- > 0) RenderStep();
        if (!condition()) throw new InvalidOperationException("The feeding timeline failed to reach its bounded target.");
    }
    internal bool TryWork()
    {
        if (InteractionsUnavailable) return false;
        _care.StartWork(Time.GetUtcNow()); _behavior.SetWorkState(CareState.IsWorking, false); return CareState.IsWorking;
    }
    internal bool TryPause()
    {
        if (IsFeeding || WorkInProgress) return false;
        _behavior.SetPaused(true); return true;
    }
    internal void FinishAndClose()
    {
        if (WasClosed) return;
        RenderUntil(() => !IsFeeding);
        Close(); DrainDispatcher();
    }
    internal static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}

internal sealed class CarePanel { internal void Refresh() { } }
internal sealed class TestTime : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    internal void Advance(double seconds) => _now = _now.AddSeconds(seconds);
}
