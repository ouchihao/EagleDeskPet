using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    // Opt-in integration check: the test runner supplies an isolated data directory.
    // This renders this app's own visual tree, not a desktop/screen capture.
    private async Task RunSmokeTestIfRequestedAsync()
    {
        string? output = Environment.GetEnvironmentVariable("EAGLE_PET_SMOKE_DIR");
        string? data = Environment.GetEnvironmentVariable("EAGLE_PET_DATA_DIR");
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(data)) return;
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        try
        {
            if (Environment.GetEnvironmentVariable("EAGLE_PET_SMOKE_MODE") == "expansion")
            {
                await RunExpansionSmokeAsync(output);
                return;
            }
            if (Environment.GetEnvironmentVariable("EAGLE_PET_SMOKE_MODE") == "features")
            {
                await RunFeaturesSmokeAsync(output);
                return;
            }
            if (Environment.GetEnvironmentVariable("EAGLE_PET_SMOKE_MODE") == "work")
            {
                await RunWorkSmokeAsync(output);
                return;
            }
            _settings.IsPaused = false;
            _behavior.SetPaused(false);
            OpenCarePanel();
            int before = CareState.Food;
            FeedPet();
            if (CareState.Food != before - 1 || CareState.Fullness < 99)
                throw new InvalidOperationException("Feed integration did not update care state.");
            RenderOwnVisual(_carePanel!, Path.Combine(output, "care-panel.png"));
            ShowDemoNotification("联调测试");
            RenderOwnVisual(_bubble!, Path.Combine(output, "notification-bubble.png"));
            var observed = new HashSet<ClipKind>();
            var started = System.Diagnostics.Stopwatch.StartNew();
            while (started.Elapsed.TotalSeconds < 14)
            {
                await Task.Delay(25);
                observed.Add(_behavior.CurrentSample.Kind);
                if (_behavior.CurrentSample.Kind == ClipKind.Eat && _behavior.CurrentSample.Progress is > 0.45 and < 0.55)
                    RenderOwnVisual(Root, Path.Combine(output, "pet-eating.png"));
            }
            if (!observed.Contains(ClipKind.Eat)) throw new InvalidOperationException("Eat never played.");
            _store.Save(CareState);
            var reloaded = new PetStore().Load() ?? throw new InvalidOperationException("Save did not reload.");
            if (reloaded.Food != CareState.Food || reloaded.TotalMeals != CareState.TotalMeals)
                throw new InvalidOperationException("Save round-trip differs from UI state.");
            File.WriteAllText(Path.Combine(output, "gui-smoke.json"), JsonSerializer.Serialize(new
            {
                passed = true, assetsPredecoded = true, food = CareState.Food, meals = CareState.TotalMeals,
                observedActions = observed.Select(x => x.ToString()).ToArray(), dataDirectory = AppPaths.DataDirectory,
                limitations = "Rendered own WPF visuals; manual mouse drag and sustained physical-display 60 FPS are not certified by this check."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "gui-smoke.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
        }
        finally { Close(); }
    }

    private static void RenderOwnVisual(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        // A WPF Window's native title bar is not part of its visual tree. Render
        // the client content at its actual size instead of adding a blank strip.
        if (element is Window { Content: FrameworkElement content }) element = content;
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
