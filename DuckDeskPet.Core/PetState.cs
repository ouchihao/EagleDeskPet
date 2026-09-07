using System.Text.Json.Serialization;

namespace DuckDeskPet.Core;

/// <summary>Local game progress only; no conversations or AI memory are stored.</summary>
public sealed class PetState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public double Fullness { get; set; } = 75.0;
    public double Mood { get; set; } = 70.0;
    public int Food { get; set; } = 5;
    public int Experience { get; set; }
    public double FoodProgressSeconds { get; set; }
    public DateTimeOffset LastUpdatedUtc { get; set; }
    public DateTimeOffset? LastFedUtc { get; set; }
    public DateTimeOffset? LastPettedUtc { get; set; }
    public int TotalMeals { get; set; }
    public int TotalPets { get; set; }
    public bool IsWorking { get; set; }
    public double WorkSessionSeconds { get; set; }
    public double WorkExperienceProgressSeconds { get; set; }
    public double TotalWorkSeconds { get; set; }
    public List<string> Achievements { get; set; } = new();

    [JsonIgnore]
    public int Level => 1 + Math.Clamp(Experience, 0, 99_900) / 100;

    [JsonIgnore]
    public bool IsHungry => Fullness < 30.0;

    [JsonIgnore]
    public bool IsBusy => IsWorking && WorkSessionSeconds >= PetCareService.BusyAfterSeconds - 1e-7;

    [JsonIgnore]
    public double FoodProgress => Math.Clamp(FoodProgressSeconds / PetCareService.FoodIntervalSeconds, 0.0, 1.0);
}
