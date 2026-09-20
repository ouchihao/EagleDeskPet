namespace DuckDeskPet.Core;

public enum PetCareStatus
{
    Fed,
    Petted,
    NoFood,
    Full,
    Cooldown,
    Working,
    WorkStarted,
    WorkStopped,
    AlreadyWorking,
    NotWorking,
    TooHungryToWork,
    Initializing,
}

public readonly record struct PetCareResult(PetCareStatus Status, string Message)
{
    public bool Success => Status is PetCareStatus.Fed or PetCareStatus.Petted or PetCareStatus.WorkStarted or PetCareStatus.WorkStopped;
    public bool Changed => Success;
}

/// <summary>
/// Wall-clock game simulation, deliberately independent from the clamped animation
/// clock. Long absences and suspended runs receive at most two hours of catch-up.
/// </summary>
public sealed class PetCareService
{
    public const double FoodIntervalSeconds = 300.0;
    public const int MaximumFood = 99;
    public const double MaximumCatchUpSeconds = 7200.0;
    public const double FullnessLossPerHour = 4.0;
    public const double PetCooldownSeconds = 10.0;
    public const double FeedCooldownSeconds = 2.0;
    public const int MaximumExperience = GrowthPolicy.MaximumExperience;
    public const double BusyAfterSeconds = 1800.0;
    public const double WorkFullnessLossPerHour = 12.0;
    public const double WorkMoodLossPerHour = 10.0;
    public const double WorkExperienceIntervalSeconds = 60.0;

    private static readonly HashSet<string> KnownAchievements =
        new(HonorCatalog.Definitions.Select(x => x.Id), StringComparer.Ordinal);

    private Dictionary<ContentSlot, string> _effectiveEquipment;
    public bool IsCatchUpPending { get; private set; }

    public PetCareService(PetState? saved, DateTimeOffset now,
        Func<ContentDefinition, bool>? isEquipmentAvailable = null, bool deferInitialAdvance = false)
    {
        State = Normalize(saved, now.ToUniversalTime());
        // The host probes installed resources before offline settlement. A saved
        // request whose resources fell back must not retain its better statistics.
        isEquipmentAvailable ??= _ => true;
        _effectiveEquipment = Enum.GetValues<ContentSlot>().ToDictionary(slot => slot,
            slot => ContentOwnershipService.ResolveEquipped(State.Content, slot, isEquipmentAvailable).EffectiveId
                ?? ContentCatalog.DefaultForSlot(slot));
        IsCatchUpPending = deferInitialAdvance;
        if (!IsCatchUpPending) Advance(now);
    }

    public PetState State { get; }
    /// <summary>Actual wages credited during this app run, including eligible startup catch-up; purchases do not reduce it.</summary>
    public decimal EarnedCoinsThisRun { get; private set; }
    public EquipmentBonuses CurrentBonuses => IsCatchUpPending ? EquipmentBonuses.None
        : EquipmentPolicy.Resolve(State.Content, _effectiveEquipment);
    public decimal MoneyPerWorkMinute => EconomyPolicy.CoinsPerWorkMinute * (1m + CurrentBonuses.WorkCoinBonus);
    public decimal WorkExperiencePerMinute => 1m + CurrentBonuses.WorkExperienceBonus;

    /// <summary>
    /// Called only after the host has actually applied an owned outfit/prop (or
    /// fallback), never for a shop preview or pending selection. Settle the prior
    /// interval before changing rates, even when the change occurs mid-minute.
    /// </summary>
    public void SetEffectiveEquipment(DateTimeOffset now, IReadOnlyDictionary<ContentSlot, string> equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        if (IsCatchUpPending) throw new InvalidOperationException("BeginCatchUp must confirm initial equipment before changing it.");
        Advance(now);
        SetEquipmentSnapshot(equipment);
    }

    /// <summary>
    /// Complete deferred startup once installed resources and the actual renderer
    /// selection are known. Loading-time timer ticks cannot consume the old saved
    /// interval at default rates. Repeated completion cannot replay the catch-up.
    /// </summary>
    public bool BeginCatchUp(DateTimeOffset now, IReadOnlyDictionary<ContentSlot, string> actualEffectiveEquipment)
    {
        ArgumentNullException.ThrowIfNull(actualEffectiveEquipment);
        if (!IsCatchUpPending) return false;
        SetEquipmentSnapshot(actualEffectiveEquipment);
        IsCatchUpPending = false;
        Advance(now);
        return true;
    }

    private void SetEquipmentSnapshot(IReadOnlyDictionary<ContentSlot, string> equipment)
    {
        _effectiveEquipment = Enum.GetValues<ContentSlot>().ToDictionary(slot => slot,
            slot => equipment.TryGetValue(slot, out string? id) && ContentCatalog.TryGet(id, out var item) &&
                item!.Slot == slot && ContentOwnershipService.Owns(State.Content, id)
                ? id : ContentCatalog.DefaultForSlot(slot));
    }

    private static PetCareResult InitializingResult => new(PetCareStatus.Initializing, "装备还在就位，马上就好。");

    /// <summary>
    /// Returns whether game progress or its saved timestamp changed. A backwards
    /// clock does not earn food, undo hunger, or bypass interaction cooldowns.
    /// </summary>
    public bool Advance(DateTimeOffset now)
    {
        if (IsCatchUpPending) return false;
        now = now.ToUniversalTime();
        if (now <= State.LastUpdatedUtc)
        {
            return false;
        }

        DateTimeOffset previousUpdate = State.LastUpdatedUtc;
        double seconds = Math.Min((now - previousUpdate).TotalSeconds, MaximumCatchUpSeconds);
        State.LastUpdatedUtc = now;
        var bonuses = CurrentBonuses;
        double fullnessMultiplier = (double)(1m - bonuses.FullnessDecayReduction);
        double moodMultiplier = (double)(1m - bonuses.MoodDecayReduction);
        double workSeconds = State.IsWorking
            ? Math.Min(seconds, State.Fullness * 3600.0 / (WorkFullnessLossPerHour * fullnessMultiplier))
            : 0.0;
        double idleSeconds = seconds - workSeconds;
        State.Fullness = Math.Max(0.0, State.Fullness -
            (workSeconds * WorkFullnessLossPerHour + idleSeconds * FullnessLossPerHour) * fullnessMultiplier / 3600.0);
        State.Mood = Math.Max(0.0, State.Mood -
            (workSeconds * WorkMoodLossPerHour + idleSeconds) * moodMultiplier / 3600.0);
        if (workSeconds > 0.0)
        {
            State.WorkSessionSeconds = Math.Min(1e12, State.WorkSessionSeconds + workSeconds);
            State.TotalWorkSeconds = Math.Min(1e12, State.TotalWorkSeconds + workSeconds);
            decimal accumulatedWork = State.WorkExperienceRemainderUnits +
                EconomyPolicy.ExactSeconds(workSeconds) * (1m + bonuses.WorkExperienceBonus);
            int workExperience = (int)decimal.Floor(accumulatedWork / 60m);
            State.WorkExperienceRemainderUnits = accumulatedWork - workExperience * 60m;
            State.WorkExperienceProgressSeconds = (double)State.WorkExperienceRemainderUnits;
            AddExperience(workExperience);
            // The effective work interval starts at the prior simulation timestamp.
            // Keeping a separate wage high-water mark prevents clock rollback plus
            // a restart from paying that same wall-clock interval again. A v1
            // migration starts this mark at 'now', so old catch-up earns no wages.
            double unpaidPrefix = Math.Max(0, (State.WageLastUpdatedUtc - previousUpdate).TotalSeconds);
            EarnedCoinsThisRun += EconomyPolicy.SettleWages(State, Math.Max(0, workSeconds - unpaidPrefix),
                1m + bonuses.WorkCoinBonus);
        }
        if (now > State.WageLastUpdatedUtc) State.WageLastUpdatedUtc = now;

        if (State.Fullness <= 1e-9)
        {
            State.Fullness = 0.0;
            State.IsWorking = false;
        }

        if (State.Food >= MaximumFood)
        {
            // A full bag cannot bank an unlimited instant refill after feeding.
            State.FoodProgressSeconds = 0.0;
        }
        else
        {
            double accumulated = State.FoodProgressSeconds + seconds;
            int earned = (int)Math.Floor((accumulated + 1e-7) / FoodIntervalSeconds);
            int credited = Math.Min(earned, MaximumFood - State.Food);
            State.Food += credited;
            State.FoodProgressSeconds = State.Food == MaximumFood
                ? 0.0
                : Math.Max(0.0, accumulated - earned * FoodIntervalSeconds);
            AddExperience(credited);
        }

        return true;
    }

    public PetCareResult Feed(DateTimeOffset now)
    {
        if (IsCatchUpPending) return InitializingResult;
        Advance(now);
        if (State.IsWorking)
        {
            return new(PetCareStatus.Working, "先取消工作，再给我开饭。");
        }

        if (IsCoolingDown(State.LastFedUtc, now, FeedCooldownSeconds))
        {
            return new(PetCareStatus.Cooldown, "嚼着呢，别急着续碗。");
        }

        if (State.Fullness >= 95.0)
        {
            return new(PetCareStatus.Full, "已经吃撑啦，先陪我玩一会儿。");
        }

        if (State.Food <= 0)
        {
            return new(PetCareStatus.NoFood, "粮袋空啦，挂一会儿机就有吃的了。");
        }

        State.Food--;
        State.Fullness = Math.Min(100.0, State.Fullness + 25.0);
        State.Mood = Math.Min(100.0, State.Mood + 8.0);
        State.LastFedUtc = InteractionTime(now);
        State.TotalMeals = Math.Min(1_000_000, State.TotalMeals + 1);
        AddExperience(5);
        Unlock("first-meal");
        return new(PetCareStatus.Fed, "嘴上说不要，饭一口没少。");
    }

    public PetCareResult Pet(DateTimeOffset now)
    {
        if (IsCatchUpPending) return InitializingResult;
        Advance(now);
        if (State.IsWorking)
        {
            return new(PetCareStatus.Working, "先取消工作，我再伸头给你摸。");
        }

        if (IsCoolingDown(State.LastPettedUtc, now, PetCooldownSeconds))
        {
            return new(PetCareStatus.Cooldown, "知道你喜欢我，毛都快搓秃了。");
        }

        State.Mood = Math.Min(100.0, State.Mood + 3.0);
        State.LastPettedUtc = InteractionTime(now);
        State.TotalPets = Math.Min(1_000_000, State.TotalPets + 1);
        AddExperience(1);
        if (State.TotalPets >= 10)
        {
            Unlock("gentle-hands");
        }

        return new(PetCareStatus.Petted, "手法不错，下次还找你。");
    }

    public PetCareResult StartWork(DateTimeOffset now)
    {
        if (IsCatchUpPending) return InitializingResult;
        Advance(now);
        if (State.IsWorking)
        {
            return new(PetCareStatus.AlreadyWorking, "已经在工位上了，别催啦。");
        }

        if (State.Fullness <= 0.0)
        {
            return new(PetCareStatus.TooHungryToWork, "空着肚子开不了工，先喂我一口。");
        }

        State.IsWorking = true;
        State.WorkSessionSeconds = 0.0;
        return new(PetCareStatus.WorkStarted, "电脑一开，快乐拜拜。开始工作啦。");
    }

    public PetCareResult StopWork(DateTimeOffset now)
    {
        if (IsCatchUpPending) return InitializingResult;
        Advance(now);
        if (!State.IsWorking)
        {
            return new(PetCareStatus.NotWorking, "已经下班啦。");
        }

        State.IsWorking = false;
        return new(PetCareStatus.WorkStopped, "收到，下班比上班积极。");
    }

    private DateTimeOffset InteractionTime(DateTimeOffset now) =>
        now > State.LastUpdatedUtc ? now.ToUniversalTime() : State.LastUpdatedUtc;

    private static bool IsCoolingDown(DateTimeOffset? previous, DateTimeOffset now, double seconds) =>
        previous.HasValue && (now - previous.Value).TotalSeconds < seconds;

    private void AddExperience(int amount)
    {
        State.Experience = Math.Clamp(State.Experience + amount, 0, MaximumExperience);
        if (State.Level >= 2)
        {
            Unlock("level-2");
        }
        ContentOwnershipService.GrantEligibleRewards(State.Content, State);
        HonorCatalog.RecordEarned(State);
    }

    private void Unlock(string id)
    {
        if (!State.Achievements.Contains(id, StringComparer.Ordinal))
        {
            State.Achievements.Add(id);
        }
    }

    private static PetState Normalize(PetState? saved, DateTimeOffset now)
    {
        if (saved is null)
        {
            return new PetState { LastUpdatedUtc = now, WageLastUpdatedUtc = now };
        }

        if (saved.Version is not 1 and not 2 && saved.Version != PetState.CurrentVersion)
        {
            throw new NotSupportedException($"Unsupported pet save version: {saved.Version}.");
        }

        DateTimeOffset lastUpdated = saved.LastUpdatedUtc == default ? now : saved.LastUpdatedUtc.ToUniversalTime();
        // On a new process, a save from a future system clock is rebased without
        // awarding anything. Within a process Advance retains a UTC high-water mark.
        bool futureSave = lastUpdated > now;
        bool migrating = saved.Version == 1;
        bool legacyPrecision = saved.Version < 3;
        EconomyPolicy.ValidateWalletLedger(saved);
        double totalWorkSeconds = FiniteClamp(saved.TotalWorkSeconds, 0.0, 1e12, 0.0);
        var state = new PetState
        {
            Fullness = FiniteClamp(saved.Fullness, 0.0, 100.0, 75.0),
            Mood = FiniteClamp(saved.Mood, 0.0, 100.0, 70.0),
            Food = Math.Clamp(saved.Food, 0, MaximumFood),
            Experience = legacyPrecision ? GrowthPolicy.MigrateLegacyExperience(saved.Experience)
                : Math.Clamp(saved.Experience, 0, MaximumExperience),
            FoodProgressSeconds = FiniteClamp(saved.FoodProgressSeconds, 0.0, FoodIntervalSeconds - 0.001, 0.0),
            LastUpdatedUtc = futureSave ? now : lastUpdated,
            LastFedUtc = NormalizeInteraction(saved.LastFedUtc, futureSave, now),
            LastPettedUtc = NormalizeInteraction(saved.LastPettedUtc, futureSave, now),
            // A future game-reward timestamp is a persisted high-water mark.
            // Rebasing it on restart would shorten the anti-spam cooldown.
            LastGameRewardUtc = saved.LastGameRewardUtc?.ToUniversalTime(),
            TotalMeals = Math.Clamp(saved.TotalMeals, 0, 1_000_000),
            TotalPets = Math.Clamp(saved.TotalPets, 0, 1_000_000),
            IsWorking = saved.IsWorking && FiniteClamp(saved.Fullness, 0.0, 100.0, 75.0) > 0.0,
            WorkSessionSeconds = FiniteClamp(saved.WorkSessionSeconds, 0.0, 1e12, 0.0),
            WorkExperienceProgressSeconds = FiniteClamp(saved.WorkExperienceProgressSeconds,
                0.0, WorkExperienceIntervalSeconds - 0.001, 0.0),
            TotalWorkSeconds = totalWorkSeconds,
            Coins = migrating ? 0 : decimal.Truncate(Math.Clamp(saved.Coins, 0, EconomyPolicy.MaximumCoins) * 100m) / 100m,
            // Preserve previously earned fractional work at its OLD base rate.
            // It is not multiplied by newly equipped gear or reconstructed from
            // lifetime work. The next eligible settlement converts it to cents.
            WageRemainderUnits = migrating ? 0 : legacyPrecision
                ? EconomyPolicy.ExactSeconds(FiniteClamp(saved.WageProgressSeconds, 0, 59.9999999, 0))
                : Math.Clamp(saved.WageRemainderUnits, 0, 59.9999999m),
            WorkExperienceRemainderUnits = legacyPrecision
                ? EconomyPolicy.ExactSeconds(FiniteClamp(saved.WorkExperienceProgressSeconds, 0, 59.9999999, 0))
                : Math.Clamp(saved.WorkExperienceRemainderUnits, 0, 59.9999999m),
            WageSettledWorkSeconds = migrating ? totalWorkSeconds :
                FiniteClamp(saved.WageSettledWorkSeconds, 0, 1e12, totalWorkSeconds),
            WageLastUpdatedUtc = migrating ? now :
                (saved.WageLastUpdatedUtc == default ? lastUpdated : saved.WageLastUpdatedUtc.ToUniversalTime()),
            AppliedWalletDebits = migrating ? new(StringComparer.Ordinal) :
                new(saved.AppliedWalletDebits, StringComparer.Ordinal),
            Content = ContentOwnershipService.Normalize(saved.Content),
            Achievements = (saved.Achievements ?? new()).Where(KnownAchievements.Contains).Distinct(StringComparer.Ordinal).ToList(),
        };
        state.WageProgressSeconds = (double)state.WageRemainderUnits;
        state.WorkExperienceProgressSeconds = (double)state.WorkExperienceRemainderUnits;
        ContentOwnershipService.GrantEligibleRewards(state.Content, state);
        HonorCatalog.RecordEarned(state);
        return state;
    }

    private static DateTimeOffset? NormalizeInteraction(DateTimeOffset? time, bool futureSave, DateTimeOffset now)
    {
        if (!time.HasValue)
        {
            return null;
        }

        return futureSave || time.Value > now ? now : time.Value.ToUniversalTime();
    }

    private static double FiniteClamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
