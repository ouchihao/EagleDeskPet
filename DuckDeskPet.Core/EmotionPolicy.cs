namespace DuckDeskPet.Core;

public enum EmotionReaction { None, Annoyed }

public readonly record struct EmotionEvaluation(EmotionReaction Reaction, bool IsLowMood);

public readonly record struct PettingDecision(bool AllowCareReward, EmotionReaction Reaction, bool IsFrequent);

/// <summary>
/// Session-level decisions only; never changes care stats or grants rewards.
/// Enabled controls autonomous reactions, not the user's repeated pet attempts.
/// Callers persist the opt-in and check resources/interaction availability before
/// consuming a decision. Emotion thresholds still update while reactions are off.
/// </summary>
public sealed class EmotionPolicy
{
    public const double LowMoodEnter = 25;
    public const double LowMoodExit = 40;
    public const double AutomaticCooldownSeconds = 300;
    public const double FrequentPetWindowSeconds = 10;
    public const int FrequentPetAttemptCount = 3;

    private readonly Queue<DateTimeOffset> _petAttempts = new();
    private DateTimeOffset? _lastObservedUtc;
    private DateTimeOffset? _lastPetAttemptUtc;
    private bool _petBurstTriggered;
    public bool Enabled { get; set; }
    public bool IsLowMood { get; private set; }
    public DateTimeOffset? LastAutomaticReactionUtc { get; private set; }

    public EmotionEvaluation Evaluate(PetState state, DateTimeOffset now, bool allowAutomaticReaction = true)
    {
        ArgumentNullException.ThrowIfNull(state);
        now = Observe(now);
        double mood = double.IsFinite(state.Mood) ? state.Mood : 100;
        IsLowMood = IsLowMood ? mood < LowMoodExit : mood <= LowMoodEnter;
        EmotionReaction reaction = EmotionReaction.None;
        if (Enabled && allowAutomaticReaction && !state.IsWorking &&
            (!LastAutomaticReactionUtc.HasValue || (now - LastAutomaticReactionUtc.Value).TotalSeconds >= AutomaticCooldownSeconds))
        {
            reaction = IsLowMood ? EmotionReaction.Annoyed : EmotionReaction.None;
            if (reaction != EmotionReaction.None) LastAutomaticReactionUtc = now;
        }
        return new(reaction, IsLowMood);
    }

    /// <summary>
    /// Call before Care.Pet. From the third attempt within ten seconds onward,
    /// AllowCareReward=false: the caller must not invoke the rewarding care action.
    /// Annoyed emits once per continuous burst, even when automatic emotion is off.
    /// Existing Care.Pet's own ten-second cooldown remains independently in force.
    /// </summary>
    public PettingDecision RegisterPetAttempt(DateTimeOffset now)
    {
        now = Observe(now);
        if (!_lastPetAttemptUtc.HasValue || (now - _lastPetAttemptUtc.Value).TotalSeconds > FrequentPetWindowSeconds)
        {
            _petAttempts.Clear();
            _petBurstTriggered = false;
        }
        _lastPetAttemptUtc = now;
        while (_petAttempts.Count > 0 && (now - _petAttempts.Peek()).TotalSeconds > FrequentPetWindowSeconds)
            _petAttempts.Dequeue();
        _petAttempts.Enqueue(now);
        while (_petAttempts.Count > FrequentPetAttemptCount) _petAttempts.Dequeue();
        bool frequent = _petAttempts.Count >= FrequentPetAttemptCount;
        bool react = frequent && !_petBurstTriggered;
        if (react) _petBurstTriggered = true;
        return new(!frequent, react ? EmotionReaction.Annoyed : EmotionReaction.None, frequent);
    }

    private DateTimeOffset Observe(DateTimeOffset now)
    {
        now = now.ToUniversalTime();
        if (_lastObservedUtc is { } previous && previous > now) return previous;
        _lastObservedUtc = now;
        return now;
    }
}
