using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    private async Task RunLive2DSmokeAsync(string output)
    {
        string mode = Environment.GetEnvironmentVariable("EAGLE_PET_SMOKE_MODE")!;
        bool review = mode == "live2d-review";
        bool business = mode is "live2d" or "live2d-business", gallery = mode != "live2d-business";
        string reviewOutfit = Environment.GetEnvironmentVariable("EAGLE_PET_REVIEW_OUTFIT") ?? OutfitCatalog.DefaultId;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(630));
        var token = deadline.Token;
        var clock = Stopwatch.StartNew();
        var cases = new List<ExpansionCase>();
        var clips = new List<object>();
        var combinations = new List<object>();
        long expectedNativeTerminals = business ? 1 : 0;
        string? failure = null;
        bool completed = false, isolated = false, detached = false;
        JsonElement? diagnostics = null;
        void Report() => File.WriteAllText(Path.Combine(output, "live2d-smoke.json"), JsonSerializer.Serialize(new
        {
            passed = completed && failure is null,
            completed,
            mode,
            elapsedSeconds = Math.Round(clock.Elapsed.TotalSeconds, 2),
            actualBackend = _live2D?.IsActive == true ? "live2d-native-hybrid" : "png-fallback",
            visibleSurface = _live2D?.VisibleSurface,
            hybridAssetSource = Environment.GetEnvironmentVariable("EAGLE_PET_HYBRID_ACTIONS_DIR") ?? "embedded capabilities or original default frames",
            error = failure,
            presentationWarning = PresentationWarning,
            cases,
            clips,
            combinations,
            diagnostics,
            terminalAcknowledgements = _live2D?.PresentedTerminalCount,
            nativeRasterTerminalAcknowledgements = _live2D?.NativeRasterTerminalCount,
            dataDirectory = AppPaths.DataDirectory,
            isolated,
            realHost = true,
            coverage = new
            {
                scope = review ? "partial single-outfit action review" : gallery ? "all six outfits and all scene equipment combinations" : "business only",
                requestedReviewOutfit = review ? reviewOutfit : null,
                fullGallery = gallery && !review && completed && failure is null,
                continuousVideoVerified = false,
            },
            businessClock = business ? "real production care, feeding, work and preview coordinators" : "not exercised in gallery-only mode",
            galleryClock = gallery ? "own visual samples at each catalog duration; gameplay clock stopped" : "not exercised in business-only mode",
            limitations = new[] { "Visible source recording uses real native PNG frames for Eat/Yawn and the Core canvas otherwise; no desktop capture or real user saves",
                "rAF and WPF callbacks are not physical display Present certification",
                "WebView2 MediaRecorder may omit the first native-raster span even after receiving every source selection; encoded PTS requires separate validation and these videos are not uninterrupted-display proof",
                "Native pointer drag, external drag/drop, multi-monitor DPI and long-session stability require separate checks",
                "Saved pictures and videos require human visual review; passing the protocol audit alone is not art approval" },
        }, new JsonSerializerOptions { WriteIndented = true }));
        async Task Case(string name, Func<Task> operation)
        {
            token.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            try { await operation(); cases.Add(new(name, "passed", watch.Elapsed.TotalSeconds, null)); }
            catch (Exception ex) { cases.Add(new(name, "failed", watch.Elapsed.TotalSeconds, ex.ToString())); Report(); throw; }
            Report();
        }
        void RequireLive() => ExpansionCheck(_live2D?.IsActive == true, "Expected actual Live2D renderer, not a PNG stand-in: " + PresentationWarning);
        async Task CheckStage()
        {
            RequireLive();
            using var json = JsonDocument.Parse(await _live2D!.GetStageDiagnosticsAsync().WaitAsync(token));
            diagnostics = json.RootElement.Clone();
            var data = json.RootElement;
            foreach (string key in new[] { "errors", "glErrors", "missingParameters" })
                ExpansionCheck(data.GetProperty(key).GetArrayLength() == 0, "Stage diagnostics contain " + key);
            ExpansionCheck(data.GetProperty("nonFiniteVertices").GetInt64() == 0, "Stage contains non-finite model vertices");
            ExpansionCheck(data.GetProperty("maximumFootDelta").GetDouble() == 0, "Live2D feet moved away from their planted model vertices");
            ExpansionCheck(data.GetProperty("modelsLoaded").GetInt32() == 6, "The stage did not load all six actual outfit models");
            var visibleModels = data.GetProperty("modelVisiblePixels").EnumerateObject().ToArray();
            ExpansionCheck(visibleModels.Length == 6 && visibleModels.All(x => x.Value.GetInt32() > 10),
                "Not all six official model renderers produced visible pixels during preflight");
            ExpansionCheck(data.GetProperty("frames").GetInt64() > 0 && data.GetProperty("coreUpdates").GetInt64() > 0,
                "The official Core never updated or rendered");
        }
        try
        {
            ValidateExpansionIsolation(output); isolated = true;
            await Case("isolated real host, complete model pack and transparent input surface", async () =>
            {
                RequireLive();
                _settings.IsPaused = false; _behavior.SetPaused(false); _behavior.SuspendAutomatic(true);
                _emotions.Enabled = false; _behavior.CancelAutomaticEmotions();
                SetActiveBanter(false); SetNotifications(false); SetTaskNotificationsMuted(true);
                CareState.Coins = 50000.05m; CareState.Food = 12; CareState.Fullness = 50; CareState.Mood = 70;
                ExpansionCheck(_store.Save(CareState), "Cannot seed the disposable profile: " + _store.Warning);
                ExpansionCheck(PetVisual.IsHitTestVisible && PetVisual.AllowDrop && PetVisual.ContextMenu == PetMenu,
                    "The original pet input surface is no longer active");
                ExpansionCheck(PetScale.ScaleX == 1 && PetScale.ScaleY == 1 && PetRotation.Angle == 0 && PetTranslation.X == 0 && PetTranslation.Y == 0,
                    "Raster whole-body transforms are still applied to Live2D");
                await _live2D!.CaptureStageAsync(Path.Combine(output, "live2d-initial.png")).WaitAsync(token);
                RenderOwnVisual(Root, Path.Combine(output, "live2d-wpf-composition.png"));
                await CheckStage();
            });

            if (business)
            {
                await Case("one meal reaches its actual terminal before feeding releases", async () =>
                {
                    await ExpansionIdleAsync(token);
                    int food = CareState.Food, meals = CareState.TotalMeals;
                    long acknowledgements = _live2D!.PresentedTerminalCount;
                    FeedPet(); FeedPet();
                    ExpansionCheck(CareState.Food == food - 1 && IsFeeding, "Meal admission charged more than one bowl or did not own Eat");
                    await ExpansionWaitAsync(() => _behavior.CurrentSample.Kind == ClipKind.Eat && _behavior.CurrentSample.Progress >= .55,
                        token, "Live2D Eat midpoint");
                    ExpansionCheck(_live2D.IsNativeRasterVisible, "The actual meal surface is not the original PNG action");
                    await _live2D.CaptureStageAsync(Path.Combine(output, "live2d-business-eat.png"));
                    await ExpansionWaitAsync(() => !IsFeeding && _behavior.CurrentSample.Kind == ClipKind.Idle, token, "acknowledged meal completion");
                    RequireLive();
                    ExpansionCheck(_live2D.PresentedTerminalCount > acknowledgements && _live2D.NativeRasterTerminalCount > 0 && CareState.TotalMeals == meals + 1 && CareState.Food == food - 1,
                        "Meal did not finish through its real render acknowledgement");
                });
                await Case("pause freezes the model and resume preserves the original input path", async () =>
                {
                    PauseMenuItem_OnClick(PauseMenuItem, new RoutedEventArgs(MenuItem.ClickEvent));
                    await ExpansionWaitAsync(() => _behavior.IsPaused && !_pauseChanging, token, "pause");
                    await Task.Delay(150, token);
                    string first = Path.Combine(output, "live2d-paused-a.png"), second = Path.Combine(output, "live2d-paused-b.png");
                    await _live2D!.CaptureStageAsync(first); await Task.Delay(350, token); await _live2D.CaptureStageAsync(second);
                    ExpansionCheck(SHA256.HashData(File.ReadAllBytes(first)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(second))),
                        "Paused Live2D continued to change its rendered pose");
                    PauseMenuItem_OnClick(PauseMenuItem, new RoutedEventArgs(MenuItem.ClickEvent));
                    await ExpansionWaitAsync(() => !_behavior.IsPaused && !_pauseChanging, token, "resume");
                    PetMenu.PlacementTarget = PetVisual; PetMenu.IsOpen = true; await Task.Delay(120, token);
                    ExpansionCheck(RendererStatusMenuItem.Header.ToString()!.Contains("Live2D"), "Renderer status is not visible in the real context menu");
                    RenderOwnVisual(PetMenu, Path.Combine(output, "live2d-context-menu.png")); PetMenu.IsOpen = false;
                });
                await Case("real work enters, loops, crosses thirty minutes and fully exits", async () =>
                {
                    CareState.Fullness = 90;
                    long acknowledgements = _live2D!.PresentedTerminalCount;
                    await StartWorkingAsync().WaitAsync(token);
                    await ExpansionWaitAsync(() => _behavior.CurrentSample.Kind == ClipKind.WorkLoop, token, "Live2D work loop");
                    await _live2D.CaptureStageAsync(Path.Combine(output, "live2d-business-work.png"));
                    CareState.WorkSessionSeconds = 1799.5;
                    await ExpansionWaitAsync(() => _behavior.CurrentSample.Kind == ClipKind.BusyLoop, token, "Live2D busy loop");
                    await _live2D.CaptureStageAsync(Path.Combine(output, "live2d-business-busy.png"));
                    ExpansionCheck(CareState.IsBusy && _live2D.PresentedTerminalCount >= acknowledgements + 2, "Work skipped its acknowledged scene phases");
                    StopWorking(); await ExpansionIdleAsync(token); RequireLive();
                });
                await Case("equipment commits only after the complete matching appearance is presented", async () =>
                {
                    await ExpansionIdleAsync(token);
                    foreach (string id in new[] { ContentCatalog.MintDeskId, ContentCatalog.MidnightComputerId, ContentCatalog.OfficeOutfitId })
                    {
                        var purchase = _contentTransactions!.Purchase(id);
                        ExpansionCheck(purchase.Success, "Isolated equipment purchase failed: " + id);
                        var equip = await EquipContentAsync(id).WaitAsync(token);
                        ExpansionCheck(equip.Success, "Isolated equipment preparation failed: " + id);
                    }
                    RequireLive();
                    ExpansionCheck(_live2D!.ActiveEquipment == RasterEquipment && _live2D.ActiveEquipment!.Outfit == "outfit.office",
                        "Live2D appearance differs from its fully prepared fallback set");
                    SynchronizeEquipmentBonuses();
                    var expected = EquipmentPolicy.Resolve(CareState.Content, new Dictionary<ContentSlot, string>
                    {
                        [ContentSlot.Desk] = ContentCatalog.MintDeskId,
                        [ContentSlot.Computer] = ContentCatalog.MidnightComputerId,
                        [ContentSlot.Outfit] = ContentCatalog.OfficeOutfitId,
                    });
                    ExpansionCheck(CurrentEquipmentBonuses == expected, "Equipment bonuses do not match the acknowledged appearance");
                    await _live2D.CaptureStageAsync(Path.Combine(output, "live2d-business-equipment.png"));
                });
                await Case("shop and hungry previews use separate Live2D hosts without changing ownership", async () =>
                {
                    string before = ExpansionContentFingerprint();
                    await PreviewContentAsync(ContentCatalog.MintDeskId).WaitAsync(token);
                    ExpansionCheck(_contentPreviewWindow?.IsLive2D == true, "Shop preview is still using PNG");
                    await Task.Delay(250, token); RenderOwnVisual(_contentPreviewWindow!, Path.Combine(output, "live2d-shop-preview.png"));
                    _contentPreviewWindow!.Close();
                    HungryPreviewMenu_OnClick(this, new RoutedEventArgs());
                    await ExpansionWaitAsync(() => _hungryScenePreview?.IsPrepared == true, token, "independent hungry preview", 70);
                    ExpansionCheck(_hungryScenePreview!.IsLive2D, "Hungry preview is still using PNG");
                    await Task.Delay(500, token); RenderOwnVisual(_hungryScenePreview, Path.Combine(output, "live2d-hungry-preview.png"));
                    _hungryScenePreview.Close();
                    ExpansionCheck(ExpansionContentFingerprint() == before, "A read-only preview changed actual equipment or ownership");
                    RequireLive();
                });
            }

            if (gallery)
            {
                await ExpansionIdleAsync(token);
                UnsubscribeRendering(); _careTimer.Stop(); _clickTimer.Stop(); StopReminders();
                _assetsReady = false; _notices.Clear(); DismissBubble();
                _live2D!.PresentationChanged -= OnPresentationChanged; detached = true;
                string stateBefore = JsonSerializer.Serialize(CareState.CreateSnapshot());
                long sequence = 10000;
                ClipKind[] order = [ClipKind.Yawn, ClipKind.Shy, ClipKind.Eat,
                    ClipKind.WorkEnter, ClipKind.WorkLoop, ClipKind.WorkExit,
                    ClipKind.WorkEnter, ClipKind.WorkToBusy, ClipKind.BusyLoop, ClipKind.BusyExit,
                    ClipKind.HungryEnter, ClipKind.HungryLoop, ClipKind.HungryExit, ClipKind.Tea,
                    ClipKind.Annoyed];
                string[] outfits = OutfitCatalog.Load().Outfits.Select(x => x.Id).ToArray();
                string[] desks = ContentCatalog.Definitions.Where(x => x.Type == ContentType.Desk).Select(x => x.ScenePropId!).Distinct().ToArray();
                string[] computers = ContentCatalog.Definitions.Where(x => x.Type == ContentType.Computer).Select(x => x.ScenePropId!).Distinct().ToArray();
                ExpansionCheck(order.Distinct().Count() == 14 && outfits.Length == 6 && desks.Length == 7 && computers.Length == 7,
                    "Gallery coverage differs from the installed 14 / 6 / 7 / 7 catalog");
                if (review)
                {
                    ExpansionCheck(outfits.Contains(reviewOutfit, StringComparer.Ordinal), "Unknown requested review outfit: " + reviewOutfit);
                    outfits = [reviewOutfit];
                }
                for (int outfitIndex = 0; outfitIndex < outfits.Length; outfitIndex++)
                {
                    string outfit = outfits[outfitIndex];
                    await Case("all fourteen full-length actions: " + outfit, async () =>
                    {
                        var equipment = new PresentationEquipment(desks[outfitIndex % desks.Length], computers[outfitIndex % computers.Length], outfit);
                        var idle = new ClipSample(ClipKind.Idle, ClipPlaybackPhase.Idle, 0, ++sequence);
                        await _live2D.SelectEquipmentAsync(equipment, idle, paused: true).WaitAsync(token); RequireLive();
                        if (outfitIndex == 0 && _live2D.UsesNativeAction(ClipKind.Yawn))
                        {
                            await Case("hybrid entry, raster playback and terminal handoff all preserve pause", async () =>
                            {
                                var start = new ClipSample(ClipKind.Yawn, ClipPlaybackPhase.Playing, 0, ++sequence);
                                _live2D.Present(start, paused: true, force: true);
                                await Task.Delay(200, token);
                                ExpansionCheck(!_live2D.IsNativeRasterVisible && !_live2D.CanAdvance(start), "Paused neutral entry crossed into the PNG action");
                                _live2D.Present(start, paused: false, force: true);
                                await ExpansionWaitAsync(() => _live2D.CanAdvance(start), token, "resumed neutral entry", 6);
                                var middle = new ClipSample(ClipKind.Yawn, ClipPlaybackPhase.Playing, .45, start.Sequence);
                                _live2D.Present(middle, paused: true, force: true);
                                string a = Path.Combine(output, "hybrid-paused-a.png"), b = Path.Combine(output, "hybrid-paused-b.png");
                                await _live2D.CaptureStageAsync(a); await Task.Delay(200, token); await _live2D.CaptureStageAsync(b);
                                ExpansionCheck(_live2D.IsNativeRasterVisible && SHA256.HashData(File.ReadAllBytes(a)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(b))),
                                    "Paused native frame changed or selected the hidden WebGL picture");
                                var terminal = new ClipSample(ClipKind.Yawn, ClipPlaybackPhase.Terminal, 1, start.Sequence);
                                _live2D.Present(terminal, paused: true, force: true);
                                await Task.Delay(100, token);
                                ExpansionCheck(_live2D.IsNativeRasterVisible && !_live2D.CanAdvance(terminal), "Paused native terminal crossed the exit handoff");
                                _live2D.Present(terminal, paused: false, force: true);
                                await ExpansionWaitAsync(() => _live2D.CanAdvance(terminal), token, "resumed native terminal", 6);
                                ExpansionCheck(!_live2D.IsNativeRasterVisible, "The completed native action did not return to the acknowledged Core neutral");
                            });
                        }
                        idle = new ClipSample(ClipKind.Idle, ClipPlaybackPhase.Idle, 0, ++sequence);
                        _live2D.Present(idle, paused: false, force: true);
                        await Task.Delay(150, token);
                        await _live2D.CaptureStageAsync(Path.Combine(output, outfit + "-Idle.png"));
                        await _live2D.BeginStageRecordingAsync();
                        var captured = new HashSet<ClipKind>();
                        foreach (ClipKind kind in order)
                        {
                            token.ThrowIfCancellationRequested(); RequireLive();
                            var definition = ClipCatalog.GetDefinition(kind);
                            bool native = _live2D.UsesNativeAction(kind);
                            long clipSequence = ++sequence, before = _live2D.PresentedTerminalCount;
                            bool first = captured.Add(kind);
                            double[] marks = !first ? [] : review ? [.2, .4, .6, .8] : [.25, .6];
                            int mark = 0;
                            var initial = new ClipSample(kind, ClipPlaybackPhase.Playing, 0, clipSequence);
                            _live2D.Present(initial, paused: false, force: true);
                            await ExpansionWaitAsync(() =>
                            {
                                RequireLive();
                                _live2D.Present(initial, paused: false);
                                return _live2D.CanAdvance(initial);
                            }, token, outfit + " / " + kind + " first visible frame", 6);
                            if (review && first)
                            {
                                _live2D.Present(new(kind, ClipPlaybackPhase.Playing, 0, clipSequence), paused: true, force: true);
                                await Task.Delay(40, token);
                                await _live2D.CaptureStageAsync(Path.Combine(output, $"{outfit}-{kind}-0.00.png"));
                            }
                            var clipClock = Stopwatch.StartNew();
                            _live2D.Present(new(kind, ClipPlaybackPhase.Playing, 0, clipSequence), paused: false, force: true);
                            while (clipClock.Elapsed.TotalSeconds < definition.DurationSeconds)
                            {
                                double progress = Math.Clamp(clipClock.Elapsed.TotalSeconds / definition.DurationSeconds, 0, 1);
                                _live2D.Present(new(kind, ClipPlaybackPhase.Playing, progress, clipSequence), paused: false);
                                if (mark < marks.Length && progress >= marks[mark])
                                {
                                    ExpansionCheck(_live2D.IsNativeRasterVisible == native,
                                        "Visible action backend does not match the hybrid routing policy: " + kind);
                                    await _live2D.CaptureStageAsync(Path.Combine(output, $"{outfit}-{kind}-{marks[mark]:0.00}.png"));
                                    mark++;
                                }
                                await Task.Delay(16, token);
                            }
                            var terminal = new ClipSample(kind, ClipPlaybackPhase.Terminal, 1, clipSequence);
                            _live2D.Present(terminal, paused: false, force: true);
                            await ExpansionWaitAsync(() =>
                            {
                                RequireLive();
                                _live2D.CanAdvance(terminal);
                                return _live2D.PresentedTerminalCount > before;
                            }, token, outfit + " / " + kind + " real terminal", 3);
                            if (first) await _live2D.CaptureStageAsync(Path.Combine(output, $"{outfit}-{kind}-1.00.png"));
                            if (native) expectedNativeTerminals++;
                            clips.Add(new
                            {
                                outfit,
                                kind = kind.ToString(),
                                durationSeconds = definition.DurationSeconds,
                                actualSeconds = clipClock.Elapsed.TotalSeconds,
                                sequence = clipSequence,
                                backend = native ? "native-raster" : "live2d-core",
                                terminalAcknowledged = true
                            });
                        }
                        string recordingName = review ? "live2d-review-" + outfit + ".webm" :
                            outfit == OutfitCatalog.DefaultId ? "live2d-all-actions.webm" : outfit + "-all-actions.webm";
                        await _live2D.FinishStageRecordingAsync(Path.Combine(output, recordingName)).WaitAsync(token);
                        File.WriteAllText(Path.Combine(output, outfit + "-renderer-report.json"), await _live2D.GetStageDiagnosticsAsync());
                        await CheckStage();
                    });
                }
                if (!review)
                {
                    await Case("all 294 outfit/table/computer combinations use the same semantic scene", async () =>
                    {
                        foreach (string outfit in outfits)
                            for (int d = 0; d < desks.Length; d++)
                                for (int c = 0; c < computers.Length; c++)
                                {
                                    var equipment = new PresentationEquipment(desks[d], computers[c], outfit);
                                    var sample = new ClipSample(ClipKind.WorkLoop, ClipPlaybackPhase.Playing, .6, ++sequence);
                                    await _live2D.SelectEquipmentAsync(equipment, sample, paused: true).WaitAsync(token);
                                    RequireLive();
                                    ExpansionCheck(_live2D.ActiveEquipment == equipment, "Combination preparation silently substituted equipment");
                                    combinations.Add(new { scene = "work", outfit, desk = desks[d], computer = computers[c], presented = true });
                                    if (d == c) await _live2D.CaptureStageAsync(Path.Combine(output, $"props-{outfit}-{desks[d]}-{computers[c]}.png"));
                                }
                        ExpansionCheck(combinations.Count == 294, "Working equipment combinations were not all covered");
                        await CheckStage();
                    });
                    await Case("all 42 outfit/table combinations retain the hungry scene layering", async () =>
                    {
                        foreach (string outfit in outfits)
                            foreach (string desk in desks)
                            {
                                var equipment = new PresentationEquipment(desk, computers[0], outfit);
                                var sample = new ClipSample(ClipKind.HungryLoop, ClipPlaybackPhase.Playing, .6, ++sequence);
                                await _live2D.SelectEquipmentAsync(equipment, sample, paused: true).WaitAsync(token);
                                RequireLive();
                                ExpansionCheck(_live2D.ActiveEquipment == equipment, "Hungry preparation silently substituted equipment");
                                combinations.Add(new { scene = "hungry", outfit, desk, computer = computers[0], presented = true });
                                await _live2D.CaptureStageAsync(Path.Combine(output, $"hungry-{outfit}-{desk}.png"));
                            }
                        ExpansionCheck(combinations.Count == 336, "Hungry equipment combinations were not all covered");
                        ExpansionCheck(stateBefore == JsonSerializer.Serialize(CareState.CreateSnapshot()), "Visual gallery changed paused gameplay state");
                        await CheckStage();
                    });
                }
                ExpansionCheck(stateBefore == JsonSerializer.Serialize(CareState.CreateSnapshot()), "Visual review changed paused gameplay state");
            }
            await Case("final renderer diagnostics have no missing parameters, GL errors or invalid vertices", async () =>
            {
                await CheckStage();
                ExpansionCheck(_live2D!.NativeRasterTerminalCount >= expectedNativeTerminals, "Original PNG actions did not complete their own visible handoffs");
            });
            completed = true;
        }
        catch (Exception ex) { failure = ex.ToString(); completed = true; }
        finally
        {
            Report();
            if (isolated)
            {
                PetMenu.IsOpen = false; _contentPreviewWindow?.Close(); _hungryScenePreview?.Close();
                if (!detached)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try
                    {
                        if (CareState.IsWorking) StopWorking();
                        await ExpansionIdleAsync(cleanup.Token);
                    }
                    catch (Exception ex) { failure ??= "Bounded isolated cleanup: " + ex.Message; Report(); }
                }
            }
        }
    }
}
