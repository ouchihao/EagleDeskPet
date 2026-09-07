using System.IO;
using System.Text.Json;

namespace DuckDeskPet;

internal sealed class PetSettings
{
    private const string DirectoryName = "EagleDeskPet";
    private const string FileName = "settings.json";

    public bool IsPaused { get; set; }

    public bool IsTopmost { get; set; } = true;

    public bool ShowFps { get; set; }

    public double SizeScale { get; set; } = 1.0;

    public int? LeftPixels { get; set; }

    public int? TopPixels { get; set; }

    public static PetSettings Load()
    {
        try
        {
            string path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return new PetSettings();
            }

            PetSettings? settings = JsonSerializer.Deserialize<PetSettings>(File.ReadAllText(path));
            if (settings is null || !IsSupportedScale(settings.SizeScale))
            {
                return new PetSettings();
            }

            return settings;
        }
        catch
        {
            return new PetSettings();
        }
    }

    public void Save()
    {
        try
        {
            string path = GetSettingsPath();
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);

            string temporaryPath = path + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    this,
                    new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            // Settings are a convenience. The pet remains usable if storage is unavailable.
        }
    }

    public static bool IsSupportedScale(double scale) =>
        double.IsFinite(scale) && scale >= 0.75 && scale <= 1.35;

    private static string GetSettingsPath() => Path.Combine(AppPaths.DataDirectory, FileName);
}
