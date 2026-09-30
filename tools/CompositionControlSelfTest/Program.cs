using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DuckDeskPet;
using Microsoft.Web.WebView2.Core;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        string output = Path.Combine(Path.GetTempPath(), "EagleCompositionControl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        Console.WriteLine("Isolated output: " + output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var surface = new Grid { Background = Brushes.Transparent };
        var fallback = new Image { Source = Pixel(), Stretch = Stretch.Fill };
        surface.Children.Add(fallback);
        var window = new Window { Content = surface, Width = 240, Height = 240, Left = -12000, Top = 100,
            ShowInTaskbar = false, Title = "Isolated composition control test" };
        var results = new List<object>(); int failed = 0;
        window.Loaded += async (_, _) =>
        {
            async Task Test(string name, Func<Task> run)
            {
                try { await run(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
                catch (Exception ex) { failed++; results.Add(new { name, passed = false, error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex); }
            }
            try
            {
                await Test("normal composition host initializes and navigates its own browser", async () =>
                {
                    var control = Control(); surface.Children.Add(control);
                    try
                    {
                        await control.CompositionReady.WaitAsync(TimeSpan.FromSeconds(20));
                        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(output, "web-profile"));
                        await control.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30));
                        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        control.CoreWebView2.NavigationCompleted += (_, e) =>
                        {
                            if (e.IsSuccess) loaded.TrySetResult();
                            else loaded.TrySetException(new InvalidOperationException(e.WebErrorStatus.ToString()));
                        };
                        control.NavigateToString("<!doctype html><body style='background:#e8a53b'>composition preview</body>");
                        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
                        Check((await control.CoreWebView2.ExecuteScriptAsync("document.body.textContent")).Contains("composition preview"), "Browser content did not load");
                        control.OnApplyTemplate();
                        Check(control.CompositionReady.IsCompletedSuccessfully && window.IsVisible, "Template reapplication broke the host");
                    }
                    finally { Remove(control); }
                });
                await Test("D3D COM failure stays local and reveals the existing raster fallback", async () =>
                {
                    int failures = 0; var control = Control();
                    control.NextError = new COMException("Injected D3D device initialization failure", unchecked((int)0x8876086C));
                    control.GraphicsInitializationFailed += _ => { failures++; control.Visibility = Visibility.Collapsed; };
                    surface.Children.Add(control);
                    try
                    {
                        await Expect<COMException>(control.CompositionReady);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        Check(failures == 1 && control.Visibility == Visibility.Collapsed && fallback.IsVisible && window.IsVisible,
                            "Graphics failure escaped or hid the fallback/host");
                        control.OnApplyTemplate(); Check(failures == 1, "Failed instance retried graphics creation");
                        var bitmap = new RenderTargetBitmap(200, 200, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(surface); byte[] pixels = new byte[200 * 200 * 4]; bitmap.CopyPixels(pixels, 800, 0);
                        Check(pixels.Chunk(4).Any(p => p[0] == 30 && p[1] == 150 && p[2] == 230 && p[3] == 255), "Fallback pixels were not visible");
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(output, "com-failure-fallback.png")); encoder.Save(file);
                    }
                    finally { Remove(control); }
                });
                await Test("closing before Loaded cancels the pending initialization", async () =>
                {
                    var control = Control();
                    control.StopGraphicsInitialization();
                    await Expect<OperationCanceledException>(control.CompositionReady);
                    control.OnApplyTemplate(); Check(control.TemplateCalls == 0, "Closed control attempted graphics creation");
                    control.Dispose();
                });
                await Test("a later template COM failure notifies once after readiness already completed", async () =>
                {
                    int failures = 0; var control = Control();
                    control.GraphicsInitializationFailed += _ => { failures++; control.Visibility = Visibility.Collapsed; };
                    surface.Children.Add(control);
                    try
                    {
                        await control.CompositionReady.WaitAsync(TimeSpan.FromSeconds(20));
                        control.NextError = new COMException("Injected template device failure", unchecked((int)0x8876086C));
                        control.OnApplyTemplate(); control.OnApplyTemplate();
                        Check(failures == 1 && control.CompositionReady.IsCompletedSuccessfully &&
                            control.Visibility == Visibility.Collapsed && window.IsVisible, "Post-ready failure did not preserve the host");
                    }
                    finally { Remove(control); }
                });
                await Test("non-COM template errors are not swallowed", async () =>
                {
                    var control = Control(); surface.Children.Add(control);
                    try
                    {
                        await control.CompositionReady.WaitAsync(TimeSpan.FromSeconds(20));
                        control.NextError = new InvalidOperationException("Unrelated application defect");
                        bool propagated = false;
                        try { control.OnApplyTemplate(); } catch (InvalidOperationException) { propagated = true; }
                        Check(propagated, "A non-graphics exception was swallowed");
                    }
                    finally { Remove(control); }
                });
            }
            finally
            {
                File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = failed == 0, results,
                    scope = "Production composition-control boundary, real browser navigation and injected COM failure; not Live2D model or full preview acceptance" }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close(); app.Shutdown(failed == 0 ? 0 : 1);
            }
        };
        return app.Run(window);

        TestControl Control() => new() { Width = 200, Height = 200, IsHitTestVisible = false };
        void Remove(TestControl control)
        {
            control.StopGraphicsInitialization(); surface.Children.Remove(control); control.Dispose();
        }
    }

    private static BitmapSource Pixel() => BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 30, 150, 230, 255 }, 4);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Expect<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class TestControl : GraphicsSafeWebView2CompositionControl
    {
        internal Exception? NextError;
        internal int TemplateCalls;
        protected override void ApplyCompositionTemplate()
        {
            TemplateCalls++;
            if (NextError is { } error) { NextError = null; throw error; }
            base.ApplyCompositionTemplate();
        }
    }
}
