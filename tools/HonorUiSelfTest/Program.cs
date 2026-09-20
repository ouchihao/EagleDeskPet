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
        var state = new PetState { TotalMeals = 10, TotalPets = 55, Experience = 400, TotalWorkSeconds = 1800 };
        state.Content.OwnedContentIds.UnionWith(new[] { ContentCatalog.WalnutDeskId, ContentCatalog.MidnightComputerId });
        var window = new HonorWallWindow(() => state);
        var surface = (FrameworkElement)window.Content;
        var items = (ItemsControl)window.FindName("CardsItems");
        var scroll = (ScrollViewer)window.FindName("CardScroll");
        var assertions = new List<string>();
        var layouts = new List<object>();
        try
        {
            Layout(1000, 860);
            Assert(items.Items.Count == 6, "The first cabinet contains six cards, not an eighteen-card vertical list.");
            var images = Descendants<Image>(surface).ToArray();
            var seriesFilter = (ComboBox)window.FindName("SeriesFilter");
            Assert(((TextBlock)seriesFilter.Template.FindName("SelectedSeriesLabel", seriesFilter)).Text == "全部系列", "The styled selector shows the localized label, never a record type name.");
            Assert(images.Length == 6 && images.All(x => x.Source is BitmapSource source && source.PixelWidth > 1000), "The first six real eagle badge assets decode successfully.");
            Assert(images.All(x => x.Width == 110 && ((FrameworkElement)x.Parent).Clip is EllipseGeometry), "Medals are circularly clipped without square background tiles.");
            Assert(!Descendants<System.Windows.Shapes.Path>(surface).Any(x => x.Width == 19), "Series actions are in the engraved artwork, not pasted-on colored insignias.");
            TestMotionLifecycle(images[0]);
            Assert(((TextBlock)window.FindName("CountText")).Text.Contains("9 / 18"), "Summary counts all six series, not just the displayed page.");
            Assert(Descendants<ProgressBar>(surface).Count() == 6, "All current cards expose numeric progress bars.");
            Assert(scroll.ScrollableHeight < 1, "The normal desktop cabinet needs no vertical scrolling.");
            Save("honor-all.png");
            var allBadges = new HashSet<string>(images.Select(x => x.Source.ToString()));
            for (int page = 2; page <= 3; page++)
            {
                ((Button)window.FindName("NextPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout(1000, 860);
                var pageImages = Descendants<Image>(surface).ToArray();
                Assert(pageImages.Length == 6 && pageImages.All(x => x.Source is BitmapSource source && source.PixelWidth > 1000), $"Page {page} contains six real, decoded medal images.");
                allBadges.UnionWith(pageImages.Select(x => x.Source.ToString()));
                Save($"honor-page-{page}.png");
            }
            Assert(allBadges.Count == 18, "Every tier in all six series uses its own image, including all nine new relief medals.");
            Assert(!((Button)window.FindName("NextPageButton")).IsEnabled, "Next is disabled at the last cabinet.");
            ((Button)window.FindName("PreviousPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(((TextBlock)window.FindName("PageText")).Text.StartsWith("2 / 3"), "Previous cabinet works.");
            CheckColumns(1000, 3);
            Layout(960, 721);
            Assert(items.Items.Count <= 6 && scroll.ScrollableHeight < 1, "Default native-client size stays inside the work area without a long scroll.");
            Save("honor-default-size.png");
            Layout(960, 600);
            Assert(items.Items.Count == 3, "A shorter client switches to a single-row page instead of shrinking text.");
            Save("honor-short-window.png");
            CheckColumns(610, 2);
            Save("honor-two-columns.png");
            CheckColumns(350, 1);
            Save("honor-one-column.png");

            ((RadioButton)window.FindName("EarnedFilter")).IsChecked = true;
            Layout(1000, 860);
            Assert(items.Items.Count == 6 && ((TextBlock)window.FindName("PageText")).Text.EndsWith("9 枚"), "Earned filter resets to page one and paginates nine earned medals.");
            Save("honor-earned.png");
            ((RadioButton)window.FindName("LockedFilter")).IsChecked = true;
            Layout(1000, 860);
            Assert(items.Items.Count == 6 && ((TextBlock)window.FindName("PageText")).Text.EndsWith("9 枚"), "Locked filter paginates nine unearned medals.");
            Save("honor-locked.png");

            var series = (ComboBox)window.FindName("SeriesFilter");
            series.SelectedIndex = 4;
            Layout(1000, 860);
            Assert(items.Items.Count == 2 && ((TextBlock)window.FindName("PageText")).Text.EndsWith("2 枚"), "Work series and locked status filters intersect.");
            Save("honor-work-series.png");
            series.SelectedIndex = 0;

            var retainedSource = items.ItemsSource;
            state.Mood--;
            window.Refresh();
            Assert(ReferenceEquals(retainedSource, items.ItemsSource), "Ordinary care ticks do not rebuild cards or disrupt scrolling.");
            state.TotalMeals = 100; state.TotalPets = 300; state.Experience = 900; state.TotalWorkSeconds = 72000;
            state.Content.OwnedContentIds.UnionWith(ContentCatalog.Definitions.Select(x => x.Id));
            window.Refresh();
            Layout(1000, 860);
            Assert(items.Items.Count == 0 && ((TextBlock)window.FindName("EmptyText")).Visibility == Visibility.Visible, "Completed collection gives a readable locked-filter empty state.");
            Save("honor-complete.png");
            ((RadioButton)window.FindName("AllFilter")).IsChecked = true;
            Layout(1000, 860);
            Assert(items.Items.Count == 6 && ((TextBlock)window.FindName("CountText")).Text.Contains("18 / 18"), "Refresh picks up new unlocked honors without losing pagination.");
            Save("honor-all-earned.png");
            state = new PetState();
            window.Refresh();
            ((RadioButton)window.FindName("EarnedFilter")).IsChecked = true;
            Layout(1000, 860);
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
            Layout(width, 860);
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
