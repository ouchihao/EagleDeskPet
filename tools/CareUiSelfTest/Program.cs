using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DuckDeskPet;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.Length > 0 ? args[0] : "care-ui-output");
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var pet = new PetWindow();
        var window = new CarePanel(pet);
        var surface = (FrameworkElement)window.Content;
        var tabs = (TabControl)window.FindName("CareTabs");
        var assertions = new List<string>();
        try
        {
            Assert(window.FindName("FullnessBar") is null && window.FindName("FullnessText") is null, "No retired fullness controls remain");
            foreach (var (mood, efficiency, wage) in new[] { (0d, "50%", "0.50"), (50d, "75%", "0.75"), (100d, "100%", "1.00") })
            {
                pet.CareState.Mood = mood;
                window.Refresh();
                Assert(Text("MoodEfficiencyText").Contains(efficiency) && Text("MoodEfficiencyText").Contains(wage), $"Mood {mood} displays actual efficiency and wage");
            }
            Assert(((Button)window.FindName("FoodButton")).ToolTip?.ToString()?.Contains("不会扣粮") == true, "Full mood explains no food debit");
            pet.CareState.Mood = 0;
            window.Refresh();
            Assert(((Button)window.FindName("WorkButton")).IsEnabled, "Zero mood can still start work");
            pet.CareState.IsWorking = true;
            window.Refresh();
            Assert(((Button)window.FindName("WorkButton")).IsEnabled && !((Button)window.FindName("FoodButton")).IsEnabled, "Working keeps cancel available and blocks meal interruption");
            pet.CareState.IsWorking = false;
            pet.CareState.Mood = 70;
            window.Refresh();
            Assert(((Border)window.FindName("MigrationBanner")).Visibility == Visibility.Collapsed, "Fresh v4 state has no migration banner");
            foreach (var (width, height) in new[] { (470d, 651d), (400d, 471d) })
            {
                for (int tab = 0; tab < 3; tab++)
                {
                    tabs.SelectedIndex = tab;
                    Layout(width, height);
                    Assert(Descendants<ScrollViewer>(surface).All(x => x.ScrollableWidth < 1), $"Tab {tab} at width {width} needs no horizontal scroll");
                    Save($"care-{width}-tab-{tab}.png", width, height);
                    if (Descendants<ScrollViewer>(surface).FirstOrDefault(x => x.ScrollableHeight > 0) is { } scroll)
                    {
                        scroll.ScrollToBottom();
                        Layout(width, height);
                        Save($"care-{width}-tab-{tab}-bottom.png", width, height);
                        scroll.ScrollToTop();
                    }
                }
            }
            pet.EconomyMigrationApplied = true;
            tabs.SelectedIndex = 1;
            window.Refresh();
            Layout(470, 651);
            Assert(Text("MigrationBackupText").Contains("待首次成功保存"), "Planned migration never claims a nonexistent backup");
            Save("care-migration-pending.png", 470, 651);
            pet.EconomyMigrationBackupPath = @"C:\Users\Example\AppData\Local\EagleDeskPet\pet-state.pre-v4.0123456789abcdef.json";
            window.Refresh();
            Layout(400, 471);
            Assert(Text("MigrationBackupText").Contains(pet.EconomyMigrationBackupPath), "Confirmed backup path is available in the wallet tab");
            Save("care-migration-saved-narrow.png", 400, 471);
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            {
                passed = true, assertions,
                limitations = "Offscreen real CarePanel XAML with a UI-only adapter and real care math; no production input, saves, account or desktop capture"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {assertions.Count} care UI assertions. Own-surface screenshots: {output}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { window.Close(); app.Shutdown(); }

        string Text(string name) => ((TextBlock)window.FindName(name)).Text;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions.Add(message);
        }
        void Layout(double width, double height)
        {
            surface.Measure(new Size(width, height));
            surface.Arrange(new Rect(0, 0, width, height));
            surface.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
        }
        void Save(string name, double width, double height)
        {
            var image = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            image.Render(background);
            image.Render(surface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(output, name));
            encoder.Save(stream);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
