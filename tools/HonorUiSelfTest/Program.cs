using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DuckDeskPet;
using DuckDeskPet.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.Length > 0 ? args[0] : "honor-ui-output");
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var state = new PetState { TotalMeals = 10, TotalPets = 55, Experience = 400 };
        var window = new HonorWallWindow(() => state);
        var surface = (FrameworkElement)window.Content;
        var items = (ItemsControl)window.FindName("CardsItems");
        var scroll = (ScrollViewer)window.FindName("CardScroll");
        var assertions = new List<string>();
        var layouts = new List<object>();
        try
        {
            Layout(840, 1360);
            Assert(items.Items.Count == 9, "All nine cards are visible.");
            var images = Descendants<Image>(surface).ToArray();
            Assert(images.Length == 9 && images.All(x => x.Source is BitmapSource source && source.PixelWidth > 1000), "Nine real eagle badge assets decode successfully.");
            Assert(images.Select(x => x.Source.ToString()).Distinct().Count() == 9, "All three engraved action series have separate bronze, silver and gold assets.");
            Assert(images.All(x => x.Width == 174 && ((FrameworkElement)x.Parent).Clip is EllipseGeometry), "Large medals are circularly clipped without square background tiles.");
            Assert(!Descendants<System.Windows.Shapes.Path>(surface).Any(x => x.Width == 19), "Series actions are in the engraved artwork, not pasted-on colored insignias.");
            TestMotionLifecycle(images[0]);
            Assert(((TextBlock)window.FindName("CountText")).Text.Contains("6 / 9"), "Summary counts earned tiers.");
            Assert(Descendants<ProgressBar>(surface).Count() == 9, "All cards expose numeric progress bars.");
            Save("honor-all.png");
            CheckColumns(840, 3);
            CheckColumns(610, 2);
            Save("honor-two-columns.png");
            CheckColumns(350, 1);
            Save("honor-one-column.png");

            ((RadioButton)window.FindName("EarnedFilter")).IsChecked = true;
            Layout(840, 950);
            Assert(items.Items.Count == 6, "Earned filter shows six earned cards.");
            Save("honor-earned.png");
            ((RadioButton)window.FindName("LockedFilter")).IsChecked = true;
            Layout(840, 660);
            Assert(items.Items.Count == 3, "Locked filter shows three future gold cards.");
            Save("honor-locked.png");

            var retainedSource = items.ItemsSource;
            state.Mood--;
            window.Refresh();
            Assert(ReferenceEquals(retainedSource, items.ItemsSource), "Ordinary care ticks do not rebuild cards or disrupt scrolling.");
            state.TotalMeals = 30; state.TotalPets = 100; state.Experience = 900;
            window.Refresh();
            Layout(840, 660);
            Assert(items.Items.Count == 0 && ((TextBlock)window.FindName("EmptyText")).Visibility == Visibility.Visible, "Completed collection gives a readable locked-filter empty state.");
            Save("honor-complete.png");
            ((RadioButton)window.FindName("AllFilter")).IsChecked = true;
            Layout(840, 1360);
            Assert(items.Items.Count == 9 && ((TextBlock)window.FindName("CountText")).Text.Contains("9 / 9"), "Refresh picks up new unlocked honors.");
            Save("honor-all-earned.png");
            state = new PetState();
            window.Refresh();
            ((RadioButton)window.FindName("EarnedFilter")).IsChecked = true;
            Layout(840, 660);
            Assert(items.Items.Count == 0 && ((TextBlock)window.FindName("EmptyText")).Text.Contains("第一枚"), "New pet has a helpful earned-filter empty state.");
            Assert(((RadioButton)window.FindName("AllFilter")).Focusable && ((RadioButton)window.FindName("EarnedFilter")).Focusable, "Filter controls retain keyboard focusability.");

            File.WriteAllText(Path.Combine(output, "honor-ui-report.json"), JsonSerializer.Serialize(new
            {
                passed = true, assertions, layouts,
                capturedOwnVisualTree = true,
                touchesRealSave = false,
                note = "Offscreen WPF layout/render test, not an interactive desktop or mixed-DPI monitor test.",
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Honor UI smoke PASS: {assertions.Count} assertions. Output: {output}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally { window.Close(); app.Shutdown(); }

        void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            assertions.Add(name);
            Console.WriteLine("PASS " + name);
        }
        void TestMotionLifecycle(Image image)
        {
            var host = (Grid)((Grid)image.Parent).Parent;
            var gate = typeof(HonorWallWindow).GetMethod("CanAnimate", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert(!(bool)gate.Invoke(window, new object[] { host })!, "Hidden gallery does not allow medal animations.");
            var motionType = typeof(HonorWallWindow).GetNestedType("MedalMotion", BindingFlags.NonPublic)!;
            var motion = Activator.CreateInstance(motionType, host)!;
            motionType.GetMethod("Hover")!.Invoke(motion, new object[] { true });
            var scale = (ScaleTransform)((TransformGroup)host.RenderTransform).Children[0];
            var lift = (TranslateTransform)((TransformGroup)host.RenderTransform).Children[1];
            var sheen = Descendants<System.Windows.Shapes.Rectangle>(host).Single();
            var travel = (TranslateTransform)((TransformGroup)sheen.RenderTransform).Children[1];
            Assert(scale.HasAnimatedProperties && travel.HasAnimatedProperties, "Hover starts subtle depth and one metallic-light sweep.");
            motionType.GetMethod("Stop")!.Invoke(motion, null);
            Assert(!scale.HasAnimatedProperties && !lift.HasAnimatedProperties && !travel.HasAnimatedProperties && !sheen.HasAnimatedProperties,
                "Stopping removes all medal animation clocks.");
            Assert(scale.ScaleX == 1 && scale.ScaleY == 1 && lift.Y == 0 && sheen.Opacity == 0,
                "Stopping restores neutral geometry and hides the sheen.");
            var timer = (DispatcherTimer)typeof(HonorWallWindow).GetField("_sheenTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Assert(!timer.IsEnabled, "Hidden gallery does not run an idle animation timer.");
        }
        void Layout(double width, double height)
        {
            surface.Width = width; surface.Height = height;
            for (int i = 0; i < 3; i++)
            {
                surface.Measure(new Size(width, height));
                surface.Arrange(new Rect(0, 0, width, height));
                surface.UpdateLayout();
                typeof(HonorWallWindow).GetMethod("UpdateCardWidth", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            }
            scroll.ScrollToHome();
            surface.UpdateLayout();
        }
        void CheckColumns(int width, int expected)
        {
            Layout(width, 1040);
            var panel = Descendants<WrapPanel>(items).Single();
            var positions = panel.Children.Cast<UIElement>().Select(x => x.TranslatePoint(new Point(), panel)).ToArray();
            int columns = positions.Count(x => Math.Abs(x.Y - positions[0].Y) < 0.1);
            Assert(columns == expected, $"Responsive width {width} uses {expected} columns (actual {columns}).");
            Assert(positions.Max(x => x.X) + window.CardWidth <= panel.ActualWidth + 1, "Cards remain inside the viewport.");
            layouts.Add(new { width, columns, cardWidth = window.CardWidth, panelWidth = panel.ActualWidth });
        }
        void Save(string name)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name));
            encoder.Save(stream);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T target) yield return target;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
