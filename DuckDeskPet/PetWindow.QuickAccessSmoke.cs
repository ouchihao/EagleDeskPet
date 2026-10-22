using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DuckDeskPet;

public partial class PetWindow
{
    private async Task RunQuickAccessSmokeAsync(string output)
    {
        var cases = new List<object>();
        var elapsed = Stopwatch.StartNew();
        bool passed = false;
        string? error = null;
        void Check(bool value, string name)
        {
            cases.Add(new { name, passed = value });
            if (!value) throw new InvalidOperationException(name);
        }
        async Task Reveal(double time)
        {
            HideQuickAccess();
            Point pet = PetVisual.PointToScreen(new Point(80, 80));
            UpdateQuickAccess(pet, time);
            UpdateQuickAccess(pet, time + .2);
            await Task.Delay(230);
        }
        try
        {
            ValidateExpansionIsolation(output);
            _quickAccessTimer!.Stop();
            _careTimer.Stop();
            _reminderTimer.Stop();
            _behavior.SuspendAutomatic(true);
            _companion.ActiveBanterEnabled = false;
            string before = ExpansionContentFingerprint();
            Point pet = PetVisual.PointToScreen(new Point(80, 80));
            Point far = new(pet.X + 5000, pet.Y + 5000);
            UpdateQuickAccess(far, 0);
            Check(!_quickAccessPopup!.IsOpen, "Hidden at rest; no popup hit target");
            UpdateQuickAccess(pet, 1);
            Check(!_quickAccessPopup.IsOpen, "Passing cursor does not flash toolbar");
            nint foreground = NativeMethods.GetForegroundWindow();
            await Reveal(2);
            Check(_quickAccessPopup.IsOpen && _quickAccessBar!.IsHitTestVisible && _quickAccessBar.Opacity > .99,
                "Proximity reveals interactive toolbar");
            Check(NativeMethods.GetForegroundWindow() == foreground, "Hover reveal does not steal foreground focus");
            var bar = _quickAccessBar!;
            Point onBar = bar.PointToScreen(new Point(bar.ActualWidth / 2, bar.ActualHeight / 2));
            UpdateQuickAccess(far, 2.3);
            UpdateQuickAccess(onBar, 2.5);
            UpdateQuickAccess(onBar, 4);
            Check(_quickAccessPopup.IsOpen, "Pet-to-toolbar crossing remains open");
            RenderOwnVisual(bar, Path.Combine(output, "quick-access-icons.png"));
            RenderQuickAccessPreview(output);
            UpdateQuickAccess(far, 5);
            Check(_quickAccessRevealed, "Leaving has a grace interval");
            UpdateQuickAccess(far, 5.5);
            Check(bar.IsHitTestVisible, "Visible fading icons remain clickable");
            UpdateQuickAccess(onBar, 5.55);
            Check(_quickAccessRevealed, "Returning during fade-out reverses dismissal");
            UpdateQuickAccess(far, 5.6);
            UpdateQuickAccess(far, 6.1);
            await Task.Delay(200);
            Check(!_quickAccessPopup.IsOpen, "Fade-out destroys the popup hit area");

            await Reveal(6);
            _nativeDragInProgress = true;
            UpdateQuickAccess(pet, 7);
            Check(!_quickAccessPopup.IsOpen, "Native drag immediately hides toolbar");
            _nativeDragInProgress = false;
            await Reveal(8);
            PetMenu.IsOpen = true;
            Check(!_quickAccessPopup.IsOpen, "Context menu and quick access do not overlap");
            PetMenu.IsOpen = false;

            _behavior.SetPaused(true);
            await Reveal(9);
            Check(_quickAccessPopup.IsOpen, "Paused animation still allows quick access");
            _behavior.SetPaused(false);
            SetQuickAccessVisible(false, animate: false);
            SetQuickAccessVisible(true, animate: false);
            Check(_quickAccessPopup.IsOpen && bar.Opacity == 1 && !bar.HasAnimatedProperties,
                "Reduced-motion path is immediate");
            SetQuickAccessVisible(false, animate: false);

            string[] names = ["商店", "便签", "定时器", "主页", "荣誉室"];
            Func<Window?>[] windows = [() => _shopWindow, () => _notebookWindow, () => _reminderWindow,
                () => _carePanel, () => _honorWall];
            Check(bar.ActionButtons.Count == 5 && bar.ActionButtons.Select(AutomationProperties.GetName).SequenceEqual(names),
                "Five accessible icon buttons in requested order");
            await Reveal(11);
            Button motionButton = bar.ActionButtons[0];
            Controls.QuickAccessBar.SetButtonMotion(motionButton, true, false, true);
            await Task.Delay(190);
            var motionScale = (ScaleTransform)motionButton.Template.FindName("MotionScale", motionButton);
            Check(Math.Abs(motionScale.ScaleX - 1.11) < .001 && motionButton.ActualWidth == 32,
                $"Hover smoothly enlarges visuals without moving hit targets: scale={motionScale.ScaleX}, width={motionButton.ActualWidth}");
            Controls.QuickAccessBar.SetButtonMotion(motionButton, true, true, true);
            await Task.Delay(190);
            Check(Math.Abs(motionScale.ScaleX - .96) < .001, "Press has a soft compression response");
            Controls.QuickAccessBar.SetButtonMotion(motionButton, false, false, false);
            Check(motionScale.ScaleX == 1 && !motionScale.HasAnimatedProperties,
                "Reduced motion cancels queued hover animation");
            foreach (Button button in bar.ActionButtons)
                Check(button.ToolTip is ToolTip tip && tip.Content?.ToString() == AutomationProperties.GetName(button) &&
                    ToolTipService.GetInitialShowDelay(button) == 240,
                    AutomationProperties.GetName(button) + " has a short-delay descriptive tooltip");
            for (int i = 0; i < names.Length; i++)
            {
                await Reveal(20 + i * 3);
                bar.ActionButtons[i].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                Window? child = windows[i]();
                Check(child?.IsVisible == true && child.Owner == this && !_quickAccessPopup.IsOpen,
                    names[i] + " opens existing destination and closes toolbar");
                bar.ActionButtons[i].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(ReferenceEquals(child, windows[i]()), names[i] + " reuses its existing window");
                child!.Close();
                Check(windows[i]() is null, names[i] + " releases closed window");
            }
            Check(before == ExpansionContentFingerprint(), "Navigation preserves wallet, collection and equipment");

            var outfitCatalog = OutfitCatalog.Load();
            foreach (string outfit in new[] { "outfit.default", "outfit.office", "outfit.hoodie", "outfit.ox", "outfit.hero", "outfit.astronaut" })
            {
                var frames = await RasterFramePlayer.LoadOutfitPreviewFramesAsync(outfitCatalog, outfit);
                var artBounds = MeasureQuickAccessArt(frames[0]);
                bool headgear = outfit is "outfit.ox" or "outfit.hero" or "outfit.astronaut";
                Check(artBounds.Top > 0 && artBounds.Bottom > artBounds.Top &&
                    (headgear ? artBounds.Top < 70 : artBounds.Top == 84),
                    $"Composed neutral silhouette anchors {outfit}: top={artBounds.Top}, bottom={artBounds.Bottom}");
            }

            await StartWorkingAsync();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                await ExpansionWaitAsync(() => _behavior.CurrentSample.Kind == Core.ClipKind.WorkLoop, timeout.Token, "quick access while working");
            await Reveal(40);
            bar.ActionButtons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(_notebookWindow?.IsVisible == true && CareState.IsWorking && _behavior.IsWorkSceneActive,
                "Quick navigation preserves continuous office animation");
            _notebookWindow!.Close();

            for (int i = 0; i < 3; i++)
            {
                double scale = new[] { .8, 1d, 1.28 }[i];
                ApplySize(scale, persist: false);
                UpdateLayout();
                await Reveal(50 + i * 2);
                Check(Math.Abs(bar.ActualWidth - 208) < .1 && Math.Abs(bar.ActionButtons[0].ActualWidth - 32) < .1,
                    "Readable fixed-DIP controls at pet scale " + scale);
            }
            ApplySize(1, persist: false);
            UpdateLayout();
            var monitor = NativeMethods.MonitorFromWindow(_windowHandle, NativeMethods.MonitorDefaultToNearest);
            Check(TryGetMonitorWorkArea(monitor, out var work), "Monitor work area available");
            Check(NativeMethods.GetWindowRect(_windowHandle, out var windowRect), "Pet physical dimensions available");
            int right = work.Right - windowRect.Width, bottomEdge = work.Bottom - windowRect.Height;
            foreach (var corner in new[] { (work.Left, work.Top), (right, work.Top),
                (work.Left, bottomEdge), (right, bottomEdge) })
            {
                MoveAndClamp(corner.Item1, corner.Item2);
                await Task.Delay(140);
                await Reveal(70);
                Point top = bar.PointToScreen(new Point(0, 0));
                Point bottom = bar.PointToScreen(new Point(bar.ActualWidth, bar.ActualHeight));
                Check(top.X >= work.Left - 1 && top.Y >= work.Top - 1 &&
                    bottom.X <= work.Right + 1 && bottom.Y <= work.Bottom + 1,
                    $"Popup remains inside current monitor at {corner}: {top} to {bottom}");
            }
            HideQuickAccess();
            var cover = new Window
            {
                Owner = this, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                ShowActivated = false, Topmost = true, Width = Width + 40, Height = Height + 40,
                Left = Left - 20, Top = Top - 20, Background = Brushes.Beige
            };
            try
            {
                cover.Show();
                await Task.Delay(100);
                await Reveal(76);
                Check(!_quickAccessPopup.IsOpen, "A covered eagle does not reveal a toolbar over another window");
            }
            finally { cover.Close(); }
            HideQuickAccess(dismissUntilLeave: true);
            pet = PetVisual.PointToScreen(new Point(80, 80));
            UpdateQuickAccess(pet, 80);
            UpdateQuickAccess(pet, 82);
            Check(!_quickAccessPopup.IsOpen, "Dismissed toolbar does not reopen under stationary cursor");
            UpdateQuickAccess(far, 83);
            UpdateQuickAccess(pet, 84);
            UpdateQuickAccess(pet, 85);
            Check(_quickAccessPopup.IsOpen, "Toolbar returns after leaving and approaching again");
            StopQuickAccess();
            Check(!_quickAccessTimer.IsEnabled && !_quickAccessPopup.IsOpen, "Shutdown releases timer and popup");
            passed = true;
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            _nativeDragInProgress = false;
            File.WriteAllText(Path.Combine(output, "quick-access-smoke.json"), JsonSerializer.Serialize(new
            {
                passed, completed = true, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                actualBackend = _live2D?.IsActive == true ? "Live2D" : "PNG", cases, error,
                limitations = "Own WPF visuals and real popup layout with supplied screen points; no physical mouse injection, mixed-DPI crossing or long-run certification"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private void RenderQuickAccessPreview(string output)
    {
        // Render the production controls with the original neutral art, without capturing the desktop
        var preview = new Grid { Width = 320, Height = 300, Background = new SolidColorBrush(Color.FromRgb(245, 241, 232)) };
        var pet = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/mascot-animated-neutral.png")),
            Width = 160, Height = 174, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 24)
        };
        preview.Children.Add(pet);
        preview.Children.Add(new Controls.QuickAccessBar
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 117, 0, 0)
        });
        preview.Measure(new Size(320, 300));
        preview.Arrange(new Rect(0, 0, 320, 300));
        RenderOwnVisual(preview, Path.Combine(output, "quick-access-preview.png"));
    }
}
