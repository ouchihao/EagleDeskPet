using System.Text.Json.Serialization;

namespace DuckDeskPet.Core;

/// <summary>Local game progress only; no conversations or AI memory are stored.</summary>
public sealed class PetState
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public double Fullness { get; set; } = 75.0;
    public double Mood { get; set; } = 70.0;
    public int Food { get; set; } = 5;
    public int Experience { get; set; }
    public double FoodProgressSeconds { get; set; }
    public DateTimeOffset LastUpdatedUtc { get; set; }
    public DateTimeOffset? LastFedUtc { get; set; }
    public DateTimeOffset? LastPettedUtc { get; set; }
    public DateTimeOffset? LastGameRewardUtc { get; set; }
    public int TotalMeals { get; set; }
    public int TotalPets { get; set; }
    public bool IsWorking { get; set; }
    public double WorkSessionSeconds { get; set; }
    public double WorkExperienceProgressSeconds { get; set; }
    public double TotalWorkSeconds { get; set; }
    public int Coins { get; set; }
    public double WageProgressSeconds { get; set; }
    public double WageSettledWorkSeconds { get; set; }
    public DateTimeOffset WageLastUpdatedUtc { get; set; }
    public Dictionary<string, int> AppliedWalletDebits { get; set; } = new(StringComparer.Ordinal);
    public ContentOwnershipState Content { get; set; } = new();
    public List<string> Achievements { get; set; } = new();

    /// <summary>A detached candidate for an atomic save, never the live mutable lists.</summary>
    public PetState CreateSnapshot()
    {
        var copy = (PetState)MemberwiseClone();
        copy.Achievements = new(Achievements);
        copy.AppliedWalletDebits = new(AppliedWalletDebits, StringComparer.Ordinal);
        copy.Content = Content.CreateSnapshot();
        return copy;
    }

    /// <summary>Apply a fully committed snapshot without sharing mutable collections.</summary>
    public void ApplySnapshot(PetState committed)
    {
        Version = committed.Version;
        Fullness = committed.Fullness;
        Mood = committed.Mood;
        Food = committed.Food;
        Experience = committed.Experience;
        FoodProgressSeconds = committed.FoodProgressSeconds;
        LastUpdatedUtc = committed.LastUpdatedUtc;
        LastFedUtc = committed.LastFedUtc;
        LastPettedUtc = committed.LastPettedUtc;
        LastGameRewardUtc = committed.LastGameRewardUtc;
        TotalMeals = committed.TotalMeals;
        TotalPets = committed.TotalPets;
        IsWorking = committed.IsWorking;
        WorkSessionSeconds = committed.WorkSessionSeconds;
        WorkExperienceProgressSeconds = committed.WorkExperienceProgressSeconds;
        TotalWorkSeconds = committed.TotalWorkSeconds;
        Coins = committed.Coins;
        WageProgressSeconds = committed.WageProgressSeconds;
        WageSettledWorkSeconds = committed.WageSettledWorkSeconds;
        WageLastUpdatedUtc = committed.WageLastUpdatedUtc;
        AppliedWalletDebits = new(committed.AppliedWalletDebits, StringComparer.Ordinal);
        Content = committed.Content.CreateSnapshot();
        Achievements = new(committed.Achievements);
    }

    [JsonIgnore]
    public int Level => 1 + Math.Clamp(Experience, 0, 99_900) / 100;

    [JsonIgnore]
    public bool IsHungry => Fullness < 30.0;

    [JsonIgnore]
    public bool IsBusy => IsWorking && WorkSessionSeconds >= PetCareService.BusyAfterSeconds - 1e-7;

    [JsonIgnore]
    public double FoodProgress => Math.Clamp(FoodProgressSeconds / PetCareService.FoodIntervalSeconds, 0.0, 1.0);
}
