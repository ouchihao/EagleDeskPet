using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    private async Task RunLoafingSmokeAsync(string output)
    {
        ValidateExpansionIsolation(output);
        var clock = Stopwatch.StartNew();
        var checks = new List<string>(); string? failure = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(140));
        var token = timeout.Token;
        try
        {
            _behavior.SetPaused(false); _settings.IsPaused = false;
            _companion.ActiveBanterEnabled = _banter.Enabled = false;
            _emotions.Enabled = false;
            _careTimer.Stop();
            if (Environment.GetEnvironmentVariable("EAGLE_PET_SMOKE_MODE") == "live2d-loafing")
                ExpansionCheck(_live2D?.IsActive == true, "Expected official Live2D backend: " + PresentationWarning);
            await ExpansionIdleAsync(token);
            await StartWorkingAsync();
            await ExpansionWaitAsync(() => _behavior.CurrentSample.Kind == ClipKind.WorkLoop, token, "office loop");
            var originalEquipment = RasterEquipment;
            foreach (var kind in new[] { LoafingKind.NosePicking, LoafingKind.WoodenFish })
            {
                CareState.Loafing = kind; CareState.LoafingRemainingSeconds = 1800;
                _behavior.SetWorkState(true, false);
                await ExpansionWaitAsync(() => _loafingVisual.Phase == LoafingPhase.Throw, token, "computer throw");
                foreach (double mark in new[] { .2, .55, 1.2, 1.7, 2.3 })
                {
                    await ExpansionWaitAsync(() => _loafingVisual.Seconds >= mark, token, "throw mark");
                    await CaptureLoafingAsync(Path.Combine(output, $"{kind}-throw-{mark:0.0}.png"), token);
                }
                await ExpansionWaitAsync(() => _loafingVisual.Phase == LoafingPhase.Loop, token, "GIF loop");
                for (int i = 0; i < 7; i++)
                { await CaptureLoafingAsync(Path.Combine(output, $"{kind}-loop-{i}.png"), token); await Task.Delay(240, token); }
                _behavior.SetPaused(true);
                double pausedAt = _loafingVisual.LoopSeconds;
                await Task.Delay(250, token);
                ExpansionCheck(_loafingVisual.LoopSeconds == pausedAt, "Paused GIF continued to advance");
                _behavior.SetPaused(false);
                decimal coins = CareState.Coins; int xp = CareState.Experience;
                double work = CareState.WorkSessionSeconds;
                _care.Advance(CareState.LastUpdatedUtc.AddSeconds(1800));
                ExpansionCheck(!CareState.IsLoafing && CareState.Coins == coins && CareState.Experience == xp && CareState.WorkSessionSeconds == work,
                    "Thirty-minute break paid rewards or failed to expire");
                _behavior.SetWorkState(CareState.IsWorking, CareState.IsBusy);
                await ExpansionWaitAsync(() => _loafingVisual.Phase == LoafingPhase.Return, token, "resume transition");
                foreach (double mark in new[] { .35, 1.1, 1.8, 2.55 })
                {
                    await ExpansionWaitAsync(() => _loafingVisual.Seconds >= mark, token, "return mark");
                    await CaptureLoafingAsync(Path.Combine(output, $"{kind}-return-{mark:0.00}.png"), token);
                }
                await ExpansionWaitAsync(() => !_loafingVisual.Active, token, "same office restored");
                ExpansionCheck(CareState.IsWorking && RasterEquipment == originalEquipment &&
                    _behavior.CurrentSample.Kind is ClipKind.WorkLoop or ClipKind.BusyLoop,
                    "Return changed equipment, stopped work or inserted idle");
                checks.Add(kind + ": throw / loop / pause / 30-minute zero rewards / return passed");
            }
            CareState.Loafing = LoafingKind.NosePicking; CareState.LoafingRemainingSeconds = 1800;
            await ExpansionWaitAsync(() => _loafingVisual.Phase == LoafingPhase.Loop, token, "break before cancel");
            StopWorking();
            await ExpansionWaitAsync(() => !_loafingVisual.Active, token, "cancel restores computer");
            await ExpansionIdleAsync(token);
            ExpansionCheck(!CareState.IsLoafing && !CareState.IsWorking, "Cancellation retained a break");
            checks.Add("cancel break completes return and normal work exit");
            if (_live2D?.IsActive == true)
            {
                using var report = JsonDocument.Parse(await _live2D.GetStageDiagnosticsAsync());
                foreach (string key in new[] { "errors", "glErrors", "missingParameters" })
                    ExpansionCheck(report.RootElement.GetProperty(key).GetArrayLength() == 0, "Stage reports " + key);
                File.WriteAllText(Path.Combine(output, "stage-diagnostics.json"), report.RootElement.GetRawText());
                checks.Add("official Core no stage, WebGL or parameter errors");
            }
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            _behavior.SetPaused(false);
            File.WriteAllText(Path.Combine(output, "loafing-smoke.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null, completed = true, error = failure, checks,
                elapsedSeconds = clock.Elapsed.TotalSeconds, actualBackend = _live2D?.IsActive == true ? "live2d" : "png",
                isolatedData = AppPaths.DataDirectory,
                limitations = "Visual snapshots combine the actual Core canvas and the same native overlay draw path; physical 60 Hz and long-duration manual use are not certified"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private async Task CaptureLoafingAsync(string path, CancellationToken token)
    {
        _behavior.SetPaused(true);
        try
        {
            await Task.Delay(85, token);
            BitmapSource? stage = null;
            if (_live2D?.IsActive == true)
            {
                string corePath = path + ".core.png";
                await _live2D.CaptureStageAsync(corePath);
                var image = new BitmapImage(); image.BeginInit(); image.UriSource = new Uri(corePath);
                image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze(); stage = image;
            }
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(28, 30, 35)), null, new Rect(0, 0, 384, 346));
                if (stage is not null) dc.DrawImage(stage, new Rect(0, 0, 384, 346));
                _loafingLayer!.DrawScene(dc);
            }
            var bitmap = new RenderTargetBitmap(384, 346, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path); encoder.Save(stream);
        }
        finally { _behavior.SetPaused(false); }
    }
}
