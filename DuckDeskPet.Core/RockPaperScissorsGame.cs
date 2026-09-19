namespace DuckDeskPet.Core;

public enum RpsChoice { Rock, Paper, Scissors }
public enum RpsOutcome { PlayerWin, PetWin, Draw }
public enum RpsPhase { Idle, AwaitingChoice, Preparing, Throwing, Reacting, Completed, Cancelled }
public enum RpsCancelReason { UserExit, WindowClosed, Paused, PresentationFailed, TimedOut }
public enum RpsCue { Prepare, Throw, React, ReturnToIdle }

/// <summary>Semantic performance requests deliberately do not depend on artwork or ClipKind.</summary>
public sealed record RpsPerformance(Guid RoundId, RpsCue Cue, RpsChoice? PetChoice = null, RpsOutcome? Outcome = null);
public sealed record RpsRewardClaim(Guid RoundId, int MoodDelta, DateTimeOffset EarnedAtUtc);
public sealed record RpsSnapshot(Guid RoundId, RpsPhase Phase, RpsChoice? PlayerChoice, RpsChoice? PetChoice,
    RpsOutcome? Outcome, RpsCancelReason? Cancellation, double RoundElapsedSeconds);

/// <summary>
/// One local, voluntary round. The pet commits before player input; visual acknowledgements
/// gate disclosure, not rewards. Monotonic time drives pacing, UTC only gates the persistent reward.
/// No food, XP, currency, network, or persistent pet-state mutation belongs to this class.
/// </summary>
public sealed class RockPaperScissorsGame
{
    public const int MoodReward = 2;
    public static readonly TimeSpan RewardCooldown = TimeSpan.FromMinutes(5);
    public const double PreparationSeconds = 3;
    public const double ThrowSeconds = 2;
    public const double ReactionSeconds = 5;
    public const double MaximumRoundSeconds = 20;

    private readonly Func<int> _nextPetChoice;
    private readonly TimeProvider _time;
    private RpsChoice _petChoice;
    private RpsChoice? _playerChoice;
    private RpsOutcome _outcome;
    private long _roundStarted, _phaseStarted;
    private bool _presentationCompleted, _settlementConsumed;
    private DateTimeOffset? _lastRewardUtc;
    private DateTimeOffset? _completedUtc;
    private RpsCancelReason? _cancellation;

    public RockPaperScissorsGame(Func<int>? nextPetChoice = null, TimeProvider? timeProvider = null,
        DateTimeOffset? lastGameRewardUtc = null)
    {
        _nextPetChoice = nextPetChoice ?? (() => Random.Shared.Next(3));
        _time = timeProvider ?? TimeProvider.System;
        _lastRewardUtc = lastGameRewardUtc?.ToUniversalTime();
    }

    public Guid RoundId { get; private set; }
    public RpsPhase Phase { get; private set; }
    public bool IsActive => Phase is RpsPhase.AwaitingChoice or RpsPhase.Preparing or RpsPhase.Throwing or RpsPhase.Reacting;
    public RpsSnapshot Snapshot => new(RoundId, Phase, _playerChoice,
        Phase is RpsPhase.Throwing or RpsPhase.Reacting or RpsPhase.Completed ? _petChoice : null,
        Phase is RpsPhase.Reacting or RpsPhase.Completed ? _outcome : null, _cancellation,
        _playerChoice.HasValue ? Elapsed(_roundStarted) : 0);

    public bool StartRound()
    {
        if (IsActive) return false;
        int choice = _nextPetChoice(); // No player input exists, and no callback receives that input.
        if (choice is < 0 or > 2) throw new InvalidOperationException("Pet choice source must return 0, 1 or 2.");
        _petChoice = (RpsChoice)choice;
        _playerChoice = null;
        _presentationCompleted = _settlementConsumed = false;
        _cancellation = null;
        _completedUtc = null;
        RoundId = Guid.NewGuid();
        Phase = RpsPhase.AwaitingChoice;
        return true;
    }

    public bool Choose(RpsChoice choice)
    {
        ValidateChoice(choice);
        if (Phase != RpsPhase.AwaitingChoice) return false;
        _playerChoice = choice;
        _outcome = Resolve(choice, _petChoice);
        _roundStarted = _time.GetTimestamp();
        Begin(RpsPhase.Preparing);
        return true;
    }

    public RpsPerformance? CurrentPerformance => Phase switch
    {
        RpsPhase.Preparing => new(RoundId, RpsCue.Prepare),
        RpsPhase.Throwing => new(RoundId, RpsCue.Throw, _petChoice),
        RpsPhase.Reacting => new(RoundId, RpsCue.React, _petChoice, _outcome),
        _ => null,
    };

    public bool CompletePresentation(Guid roundId, RpsCue cue)
    {
        if (roundId != RoundId || _presentationCompleted || CurrentPerformance?.Cue != cue) return false;
        _presentationCompleted = true;
        return true;
    }

    public RpsSnapshot Advance()
    {
        if (Phase is not (RpsPhase.Preparing or RpsPhase.Throwing or RpsPhase.Reacting)) return Snapshot;
        // Sleep, an unresponsive presentation adapter or a long suspension cancels; it never silently earns a bonus.
        if (Elapsed(_roundStarted) >= MaximumRoundSeconds) { Cancel(RpsCancelReason.TimedOut); return Snapshot; }
        double minimum = Phase switch
        {
            RpsPhase.Preparing => PreparationSeconds,
            RpsPhase.Throwing => ThrowSeconds,
            _ => ReactionSeconds,
        };
        if (!_presentationCompleted || Elapsed(_phaseStarted) < minimum) return Snapshot;
        if (Phase == RpsPhase.Preparing) Begin(RpsPhase.Throwing);
        else if (Phase == RpsPhase.Throwing) Begin(RpsPhase.Reacting);
        else
        {
            Phase = RpsPhase.Completed;
            _completedUtc = _time.GetUtcNow().ToUniversalTime();
        }
        return Snapshot;
    }

    public bool Cancel(RpsCancelReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        if (Phase is RpsPhase.Idle or RpsPhase.Cancelled) return false;
        Phase = RpsPhase.Cancelled;
        _cancellation = reason;
        _settlementConsumed = true;
        _presentationCompleted = false;
        return true;
    }

    /// <summary>
    /// Takes at most one claim after a complete round. The host must atomically recheck its durable
    /// LastGameRewardUtc and save that timestamp together with +2 mood, returning success only after save.
    /// A failed/cancelled host commit is deliberately not retried by this round.
    /// </summary>
    public bool TryTakeReward(DateTimeOffset? persistedLastGameRewardUtc, out RpsRewardClaim? claim)
    {
        claim = null;
        if (Phase != RpsPhase.Completed || _settlementConsumed || !_completedUtc.HasValue) return false;
        _settlementConsumed = true;
        if (persistedLastGameRewardUtc.HasValue && (!_lastRewardUtc.HasValue || persistedLastGameRewardUtc > _lastRewardUtc))
            _lastRewardUtc = persistedLastGameRewardUtc.Value.ToUniversalTime();
        DateTimeOffset now = _time.GetUtcNow().ToUniversalTime();
        // Backward clocks cannot satisfy a cooldown; do not rebase a future reward into another free claim.
        if (now < _completedUtc || (_lastRewardUtc.HasValue && now - _lastRewardUtc.Value < RewardCooldown)) return false;
        _lastRewardUtc = now;
        claim = new(RoundId, MoodReward, now);
        return true;
    }

    public static RpsOutcome Resolve(RpsChoice player, RpsChoice pet)
    {
        ValidateChoice(player); ValidateChoice(pet);
        int result = ((int)player - (int)pet + 3) % 3;
        return result == 0 ? RpsOutcome.Draw : result == 1 ? RpsOutcome.PlayerWin : RpsOutcome.PetWin;
    }

    private static void ValidateChoice(RpsChoice choice)
    {
        if (!Enum.IsDefined(choice)) throw new ArgumentOutOfRangeException(nameof(choice));
    }
    private void Begin(RpsPhase phase)
    {
        Phase = phase;
        _phaseStarted = _time.GetTimestamp();
        _presentationCompleted = false;
    }
    private double Elapsed(long start) => Math.Max(0, _time.GetElapsedTime(start, _time.GetTimestamp()).TotalSeconds);
}
