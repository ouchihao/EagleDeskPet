using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    // Only reached through the explicitly enabled, isolated-data smoke runner.
    // Real WPF rendering and care ticks run normally; only the test save's
    // work-session threshold/fullness are advanced to avoid waiting half an hour.
    private async Task RunWorkSmokeAsync(string output)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EAGLE_PET_TEST_CHANNEL")))
            throw new InvalidOperationException("Work smoke requires an isolated test channel.");
        _settings.IsPaused = false;
        _behavior.SetPaused(false);
        SetActiveBanter(false);
        OpenCarePanel();
        var observed = new HashSet<ClipKind>();
        Point? anchor = null;
        double anchorDrift = 0;

        void Observe()
        {
            var sample = _behavior.CurrentSample;
            observed.Add(sample.Kind);
            if (ClipCatalog.IsWorkScene(sample.Kind) &&
                (sample.Kind != ClipKind.WorkEnter || sample.Progress > .15))
            {
                Point current = GetHeadScreenAnchor();
                anchor ??= current;
                anchorDrift = Math.Max(anchorDrift, (current - anchor.Value).Length);
            }
        }

        async Task Until(Func<bool> condition, string description, double timeout = 12)
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                Observe();
                if (condition()) return;
                if (elapsed.Elapsed.TotalSeconds > timeout)
                    throw new InvalidOperationException($"Timed out waiting for {description}: {_behavior.CurrentSample.Kind}.");
                await Task.Delay(20);
            }
        }

        async Task At(ClipKind kind, double progress = 0) =>
            await Until(() => _behavior.CurrentSample.Kind == kind &&
                _behavior.CurrentSample.Progress >= progress, kind.ToString());

        async Task ContinuousLoop(ClipKind kind, double seconds)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed.TotalSeconds < seconds)
            {
                Observe();
                if (_behavior.CurrentSample.Kind != kind || !CareState.IsWorking)
                    throw new InvalidOperationException($"Continuous {kind} was interrupted by {_behavior.CurrentSample.Kind}.");
                await Task.Delay(20);
            }
        }

        await StartWorkingAsync();
        if (!CareState.IsWorking) throw new InvalidOperationException("Work did not start.");
        await At(ClipKind.WorkEnter, .4);
        RenderOwnVisual(Root, Path.Combine(output, "work-entry.png"));
        await At(ClipKind.WorkLoop, .25);
        RenderOwnVisual(Root, Path.Combine(output, "work-loop.png"));
        _carePanel!.Refresh();
        RenderOwnVisual(_carePanel, Path.Combine(output, "care-panel-work.png"));
        // Gives the external MCP runner time to deliver its notification while
        // verifying more than two complete real-time loops without idle resets.
        await ContinuousLoop(ClipKind.WorkLoop, 4.5);
        CareState.WorkSessionSeconds = 1799.5;
        await At(ClipKind.WorkToBusy, .25);
        await At(ClipKind.BusyLoop, .35);
        if (!CareState.IsBusy) throw new InvalidOperationException("Busy threshold was not reached.");
        RenderOwnVisual(Root, Path.Combine(output, "busy-loop.png"));
        await ContinuousLoop(ClipKind.BusyLoop, 4.2);
        StopWorking();
        await At(ClipKind.BusyExit, .45);
        RenderOwnVisual(Root, Path.Combine(output, "work-exit.png"));
        await At(ClipKind.Idle);
        bool cancelExitVerified = !CareState.IsWorking && !_behavior.IsWorkSceneActive;
        if (!cancelExitVerified) throw new InvalidOperationException("Cancel did not finish its busy exit.");

        await StartWorkingAsync();
        await At(ClipKind.WorkLoop, .1);
        CareState.Fullness = .0001;
        await Until(() => !CareState.IsWorking, "hunger-zero stop", 3);
        await At(ClipKind.WorkExit, .3);
        await At(ClipKind.Idle);
        bool stoppedAtZero = !CareState.IsWorking && CareState.Fullness == 0;
        bool propsHidden = new[] { DeskBackImage, DeskFrontImage, LaptopImage, BusyFireImage }
            .All(item => item.Visibility == Visibility.Collapsed);
        RenderOwnVisual(Root, Path.Combine(output, "pet-idle.png"));
        _store.Save(CareState);
        var reloaded = new PetStore().Load() ?? throw new InvalidOperationException("Work save did not reload.");
        if (!stoppedAtZero || !propsHidden || reloaded.IsWorking || reloaded.Fullness != 0)
            throw new InvalidOperationException("Hunger-zero exit, props cleanup or persistence failed.");

        // Test the actual preference/bubble path without waiting another minute.
        // This only advances OfficeBanter, never the economy or work clock.
        _careTimer.Stop();
        _notices.Clear();
        DismissBubble();
        SetActiveBanter(false);
        for (int i = 0; i < 60; i++) TickBanter(1);
        bool disabledQuiet = _bubble?.IsVisible != true && !CompanionPreferences.Load().ActiveBanterEnabled;
        SetActiveBanter(true);
        for (int i = 0; i < 60; i++) TickBanter(1);
        bool banterPersisted = disabledQuiet && _bubble?.IsVisible == true && _bubbleIsBanter &&
            CompanionPreferences.Load().ActiveBanterEnabled;
        if (!banterPersisted) throw new InvalidOperationException("Banter cadence/toggle/persistence failed.");
        RenderOwnVisual(_bubble!, Path.Combine(output, "compact-banter.png"));
        if (anchorDrift > .1) throw new InvalidOperationException($"Work layout drifted {anchorDrift:0.000} pixels.");

        File.WriteAllText(Path.Combine(output, "gui-smoke.json"), JsonSerializer.Serialize(new
        {
            passed = true, workMode = true, cancelExitVerified,
            workStoppedAtZero = stoppedAtZero, propsHidden, banterTogglePersisted = banterPersisted,
            headAnchorDriftPixels = anchorDrift, decodedFramesAfterExit = _framePlayer.DecodedFrameCount,
            observedActions = observed.Select(kind => kind.ToString()).ToArray(),
            dataDirectory = AppPaths.DataDirectory,
            limitations = "Real WPF and MCP with an accelerated isolated work threshold; no manual mouse-drag or sustained physical-display 60 FPS certification."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
