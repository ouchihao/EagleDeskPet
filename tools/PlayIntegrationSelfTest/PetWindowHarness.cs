using System.Windows;
using DuckDeskPet.Core;

namespace DuckDeskPet;

// Only the shell and bitmap decoder are doubles. Production Play.cs, the WPF
// RpsWindow, behavior timelines, care simulation and atomic store run unchanged.
public partial class PetWindow : Window
{
    private readonly PetBehaviorController _behavior = new();
    private readonly RasterFramePlayer _framePlayer = new();
    private readonly PetStore _store;
    private readonly PetCareService _care;
    private bool _isClosing;
    internal bool AssetsReady = true, ContentBusy, EquipmentApplying;
    internal bool IsContentInteractionBusy => ContentBusy;
    internal bool IsContentEquipmentApplying => EquipmentApplying;
    internal bool InteractionsUnavailable => !AssetsReady || _behavior.IsPaused || CareState.IsWorking;
    internal PetState CareState => _care.State;
    internal string CareStatus { get; private set; } = "";
    internal string LastSpeech = "";
    internal bool WasClosed;
    internal PetBehaviorController Behavior => _behavior;
    internal RasterFramePlayer Frames => _framePlayer;
    internal RpsWindow? GameWindow => _gameWindow;
    internal bool OwnsAnimation => _gameOwnsAutomatic;
    internal Guid Round => _gameRound;
    internal Task ExitOperation => _gameExiting;

    internal PetWindow(string directory, TestTime time)
    {
        _gameTime = time;
        _store = new PetStore(directory); // Never resolves AppPaths or the real save.
        _care = new(_store.Load(), time.GetUtcNow());
        Width = Height = 10; Left = Top = -20000; ShowInTaskbar = false;
        Closing += (_, e) => { if (!TryCloseGameForPetExit()) e.Cancel = true; };
        Closed += (_, _) => { WasClosed = true; _isClosing = true; };
        Show();
    }
    private void Say(string text) => LastSpeech = text;
    private void PlaceCompanion(Window child, bool above) { child.Left = child.Top = -20000; }
    internal Task Perform(RpsPerformance performance, CancellationToken token = default) => PerformGameAsync(performance, token);
    internal bool Reward(RpsRewardClaim claim) => CommitGameReward(claim);
    internal void ForgetReadyClip(ClipKind kind) => _framePlayer.Missing.Add(kind);
    internal bool Persist() => _store.Save(CareState);
}

internal sealed class RasterFramePlayer
{
    internal TaskCompletionSource? WarmGate;
    internal bool FailWarm;
    internal readonly HashSet<ClipKind> Missing = new();
    internal readonly HashSet<ClipKind> Warmed = new();
    public bool IsClipReady(ClipKind kind) => Warmed.Contains(kind) && !Missing.Contains(kind);
    public async Task WarmClipsAsync(IEnumerable<ClipKind> kinds, CancellationToken token = default)
    {
        if (WarmGate is { } gate) await gate.Task.WaitAsync(token);
        if (FailWarm) throw new InvalidOperationException("Injected decode failure.");
        Warmed.UnionWith(kinds);
    }
}

internal sealed class TestTime : TimeProvider
{
    private DateTimeOffset _utc = DateTimeOffset.UtcNow;
    private long _ticks;
    public override DateTimeOffset GetUtcNow() => _utc;
    public override long GetTimestamp() => _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    internal void Advance(double seconds) { _ticks += TimeSpan.FromSeconds(seconds).Ticks; _utc = _utc.AddSeconds(seconds); }
    internal void JumpUtc(double seconds) => _utc = _utc.AddSeconds(seconds);
}
