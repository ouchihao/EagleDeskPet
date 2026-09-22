using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Live2DHostProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 2;
        string output = Path.GetFullPath(args[0]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) return 3;
        Directory.CreateDirectory(output);
        var app = new Application();
        app.DispatcherUnhandledException += (_, e) =>
        {
            File.WriteAllText(Path.Combine(output, "failure.txt"), e.Exception.ToString());
            e.Handled = true;
            Environment.ExitCode = 1;
            app.Shutdown(1);
        };
        app.Run(new ProbeWindow(output));
        return Environment.ExitCode;
    }
}

internal sealed class ProbeWindow : Window
{
    private const string Origin = "https://eaglestage.invalid/";
    private readonly string _output;
    private readonly WebView2CompositionControl _browser = new()
    {
        DefaultBackgroundColor = System.Drawing.Color.Transparent,
        IsHitTestVisible = false,
    };
    private readonly Grid _surface = new() { Background = Brushes.Transparent };
    private readonly TaskCompletionSource<JsonElement> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _rejectedMessages;

    internal ProbeWindow(string output)
    {
        _output = output;
        Title = "EagleDeskPet · isolated WebGL host probe (not a Live2D model)";
        Width = 384;
        Height = 346;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Left = 40;
        Top = 40;
        _surface.Children.Add(_browser);
        _surface.Children.Add(new Border
        {
            Width = 72, Height = 22, Background = Brushes.Magenta,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        });
        Content = _surface;
        Loaded += Run;
        Closed += (_, _) => _browser.Dispose();
    }

    private async void Run(object sender, RoutedEventArgs args)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(_output, "web-profile"));
            await _browser.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30));
            var core = _browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.SetVirtualHostNameToFolderMapping("eaglestage.invalid", Path.Combine(AppContext.BaseDirectory, "Stage"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) => e.Cancel = !string.Equals(e.Uri, Origin + "index.html", StringComparison.Ordinal);
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.ProcessFailed += (_, e) => _ready.TrySetException(new InvalidOperationException("Browser process failed: " + e.ProcessFailedKind));
            core.WebMessageReceived += (_, e) =>
            {
                if (e.Source != Origin + "index.html" || e.WebMessageAsJson.Length > 4096)
                { _rejectedMessages++; return; }
                try
                {
                    using var json = JsonDocument.Parse(e.WebMessageAsJson);
                    if (json.RootElement.GetProperty("protocol").GetInt32() == 1 &&
                        json.RootElement.GetProperty("type").GetString() == "stage-probe-ready")
                        _ready.TrySetResult(json.RootElement.Clone());
                    else _rejectedMessages++;
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
                { _rejectedMessages++; }
            };
            core.Navigate(Origin + "index.html");
            var stage = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(25));
            double readySeconds = watch.Elapsed.TotalSeconds;
            await Task.Delay(2000);
            await core.ExecuteScriptAsync("window.probe.resetMetrics()");
            await Task.Delay(10000);
            string metricsJson = await core.ExecuteScriptAsync("window.probe.metrics()");
            using var metrics = JsonDocument.Parse(metricsJson);
            using (var image = File.Create(Path.Combine(_output, "webgl.png")))
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            var dark = await CaptureCompositionAsync("wpf-dark.png", Color.FromRgb(24, 27, 32));
            var light = await CaptureCompositionAsync("wpf-light.png", Color.FromRgb(245, 241, 229));
            _surface.Background = Brushes.Transparent;
            var clear = await CaptureCompositionAsync("wpf-transparent.png", null);
            var diagnostics = new
            {
                kind = "webgl-composition-host-only-not-live2d",
                runtime = environment.BrowserVersionString,
                sdk = "1.0.4191.47",
                readySeconds,
                stage,
                scheduler = metrics.RootElement.Clone(),
                composition = new { dark, light, clear },
                rejectedMessages = _rejectedMessages,
                unverified = new[] { "Cubism model and masks", "physical Present FPS", "native pointer drag/drop", "multi-monitor DPI", "sleep and context loss", "2h working stability" },
            };
            File.WriteAllText(Path.Combine(_output, "report.json"), JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = stage.GetProperty("passed").GetBoolean() && dark.Passed && light.Passed && clear.Passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(_output, "failure.txt"), ex.ToString());
            Environment.ExitCode = 1;
        }
        finally { Close(); }
    }

    private async Task<CompositionResult> CaptureCompositionAsync(string name, Color? background)
    {
        _surface.Background = background.HasValue ? new SolidColorBrush(background.Value) : Brushes.Transparent;
        await Task.Delay(300);
        var image = new RenderTargetBitmap(384, 346, 96, 96, PixelFormats.Pbgra32);
        image.Render(_surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(Path.Combine(_output, name))) encoder.Save(stream);
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(8, 8, 1, 1), pixel, 4, 0);
        bool overlay = pixel[0] == 255 && pixel[1] == 0 && pixel[2] == 255 && pixel[3] == 255;
        image.CopyPixels(new Int32Rect(370, 330, 1, 1), pixel, 4, 0);
        bool transparency = background is { } color
            ? pixel[0] == color.B && pixel[1] == color.G && pixel[2] == color.R && pixel[3] == 255
            : pixel[3] == 0;
        image.CopyPixels(new Int32Rect(192, 200, 1, 1), pixel, 4, 0);
        bool webContentCaptured = pixel[1] > 140 && pixel[2] < 100 && pixel[3] == 255;
        return new(overlay && transparency && webContentCaptured, overlay, transparency, webContentCaptured);
    }

    private sealed record CompositionResult(bool Passed, bool WpfOverlay, bool TransparentBackground, bool WebContentCaptured);
}
