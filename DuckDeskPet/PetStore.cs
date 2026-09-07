using System.IO;
using System.Text.Json;
using DuckDeskPet.Core;

namespace DuckDeskPet;

internal static class AppPaths
{
    public static string DataDirectory => Path.GetFullPath(
        Environment.GetEnvironmentVariable("EAGLE_PET_DATA_DIR") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EagleDeskPet"));
}

internal sealed class PetStore
{
    private readonly string _path = Path.Combine(AppPaths.DataDirectory, "pet-state.json");
    public string? Warning { get; private set; }
    public bool CanSave { get; private set; } = true;

    public PetState? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            if (new FileInfo(_path).Length > 1_048_576) throw new InvalidDataException("Save too large.");
            var state = JsonSerializer.Deserialize<PetState>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("Empty save.");
            if (state.Version != PetState.CurrentVersion) throw new InvalidDataException("Unsupported save version.");
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            CanSave = false;
            Warning = "存档暂时读不了，原文件已保留。本次进度不会覆盖它。";
            return null;
        }
    }

    public bool Save(PetState state)
    {
        if (!CanSave) return false;
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state,
                new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak");
            else File.Move(temporary, _path);
            Warning = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warning = "存档没有写成功，当前进度仍在内存里。";
            return false;
        }
    }
}

internal sealed class CompanionPreferences
{
    public bool NotificationsEnabled { get; set; } = true;
    public bool ActiveBanterEnabled { get; set; } = true;
    public Dictionary<string, string> Applications { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static CompanionPreferences Load()
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDirectory, "companion.json");
            if (!File.Exists(path)) return new();
            var loaded = JsonSerializer.Deserialize<CompanionPreferences>(File.ReadAllText(path)) ?? new();
            loaded.Applications = new(loaded.Applications ?? new(), StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new();
        }
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            string path = Path.Combine(AppPaths.DataDirectory, "companion.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
