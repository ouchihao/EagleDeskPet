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
    public const int MaximumExperience = 99_900;
    public const double BusyAfterSeconds = 1800.0;
    public const double WorkFullnessLossPerHour = 12.0;
    public const double WorkMoodLossPerHour = 10.0;
    public const double WorkExperienceIntervalSeconds = 60.0;

    private static readonly HashSet<string> KnownAchievements = new(StringComparer.Ordinal)
    {
        "first-meal", "gentle-hands", "level-2",
    };

    public PetCareService(PetState? saved, DateTimeOffset now)
    {
        State = Normalize(saved, now.ToUniversalTime());
        Advance(now);
    }

    public PetState State { get; }

    /// <summary>
    /// Returns whether game progress or its saved timestamp changed. A backwards
    /// clock does not earn food, undo hunger, or bypass interaction cooldowns.
    /// </summary>
    public bool Advance(DateTimeOffset now)
    {
        now = now.ToUniversalTime();
        if (now <= State.LastUpdatedUtc)
        {
            return false;
        }

        double seconds = Math.Min((now - State.LastUpdatedUtc).TotalSeconds, MaximumCatchUpSeconds);
        State.LastUpdatedUtc = now;
        double workSeconds = State.IsWorking
            ? Math.Min(seconds, State.Fullness * 3600.0 / WorkFullnessLossPerHour)
            : 0.0;
        double idleSeconds = seconds - workSeconds;
        State.Fullness = Math.Max(0.0, State.Fullness -
            (workSeconds * WorkFullnessLossPerHour + idleSeconds * FullnessLossPerHour) / 3600.0);
        State.Mood = Math.Max(0.0, State.Mood -
            (workSeconds * WorkMoodLossPerHour + idleSeconds) / 3600.0);
        if (workSeconds > 0.0)
        {
            State.WorkSessionSeconds = Math.Min(1e12, State.WorkSessionSeconds + workSeconds);
            State.TotalWorkSeconds = Math.Min(1e12, State.TotalWorkSeconds + workSeconds);
            double accumulatedWork = State.WorkExperienceProgressSeconds + workSeconds;
            int workExperience = (int)Math.Floor((accumulatedWork + 1e-7) / WorkExperienceIntervalSeconds);
            State.WorkExperienceProgressSeconds = Math.Max(0.0,
                accumulatedWork - workExperience * WorkExperienceIntervalSeconds);
            AddExperience(workExperience);
        }

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
            return new PetState { LastUpdatedUtc = now };
        }

        if (saved.Version != PetState.CurrentVersion)
        {
            throw new NotSupportedException($"Unsupported pet save version: {saved.Version}.");
        }

        DateTimeOffset lastUpdated = saved.LastUpdatedUtc == default ? now : saved.LastUpdatedUtc.ToUniversalTime();
        // On a new process, a save from a future system clock is rebased without
        // awarding anything. Within a process Advance retains a UTC high-water mark.
        bool futureSave = lastUpdated > now;
        return new PetState
        {
            Fullness = FiniteClamp(saved.Fullness, 0.0, 100.0, 75.0),
            Mood = FiniteClamp(saved.Mood, 0.0, 100.0, 70.0),
            Food = Math.Clamp(saved.Food, 0, MaximumFood),
            Experience = Math.Clamp(saved.Experience, 0, MaximumExperience),
            FoodProgressSeconds = FiniteClamp(saved.FoodProgressSeconds, 0.0, FoodIntervalSeconds - 0.001, 0.0),
            LastUpdatedUtc = futureSave ? now : lastUpdated,
            LastFedUtc = NormalizeInteraction(saved.LastFedUtc, futureSave, now),
            LastPettedUtc = NormalizeInteraction(saved.LastPettedUtc, futureSave, now),
            TotalMeals = Math.Clamp(saved.TotalMeals, 0, 1_000_000),
            TotalPets = Math.Clamp(saved.TotalPets, 0, 1_000_000),
            IsWorking = saved.IsWorking && FiniteClamp(saved.Fullness, 0.0, 100.0, 75.0) > 0.0,
            WorkSessionSeconds = FiniteClamp(saved.WorkSessionSeconds, 0.0, 1e12, 0.0),
            WorkExperienceProgressSeconds = FiniteClamp(saved.WorkExperienceProgressSeconds,
                0.0, WorkExperienceIntervalSeconds - 0.001, 0.0),
            TotalWorkSeconds = FiniteClamp(saved.TotalWorkSeconds, 0.0, 1e12, 0.0),
            Achievements = (saved.Achievements ?? new()).Where(KnownAchievements.Contains).Distinct(StringComparer.Ordinal).ToList(),
        };
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
