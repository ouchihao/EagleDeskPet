using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is not (4 or 5)) return 2;
        string assembly = Path.GetFullPath(args[0]), runtime = Path.GetFullPath(args[1]);
        string stage = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) return 2;
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("EAGLE_PET_DATA_DIR", Path.Combine(output, "isolated-data"));
        Environment.SetEnvironmentVariable("EAGLE_PET_TEST_CHANNEL", "gaze-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("EAGLE_PET_LIVE2D_CONFIG", runtime);
        Environment.SetEnvironmentVariable("EAGLE_PET_RENDERER", null);
        Environment.SetEnvironmentVariable("EAGLE_PET_SMOKE_DIR", null);
        string stageTarget = Path.Combine(AppContext.BaseDirectory, "Live2DStage");
        Directory.CreateDirectory(stageTarget);
        foreach (string file in Directory.EnumerateFiles(stage))
            File.Copy(file, Path.Combine(stageTarget, Path.GetFileName(file)), true);
        string bin = Path.GetDirectoryName(assembly)!;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string candidate = Path.Combine(bin, name.Name + ".dll");
            return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
        };
        try
        {
            var product = AssemblyLoadContext.Default.LoadFromAssemblyPath(assembly);
            typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, product);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var probe = new Probe(product, output, args.Length == 5 && args[4] == "baseline");
            app.Startup += async (_, _) =>
            {
                try { await probe.Run(); }
                catch (Exception error) { File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString()); Environment.ExitCode = 1; }
                finally { probe.Close(); app.Shutdown(); }
            };
            File.WriteAllText(Path.Combine(output, "provenance.json"), JsonSerializer.Serialize(new
            {
                product = assembly, assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))),
                stage, runtime, isolatedData = Environment.GetEnvironmentVariable("EAGLE_PET_DATA_DIR"),
                cursorNeverMoved = true, baseline = args.Length == 5,
                stageHashes = Directory.EnumerateFiles(stage).ToDictionary(file => Path.GetFileName(file)!,
                    file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))
            }, new JsonSerializerOptions { WriteIndented = true }));
            app.Run();
            return Environment.ExitCode;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString()); return 1; }
    }
}

internal sealed class Probe(Assembly product, string output, bool baseline)
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private Window? _window;
    private object _presenter = null!, _behavior = null!, _settings = null!;
    private FrameworkElement _image = null!;
    private readonly List<object> _samples = [];
    private readonly List<object> _checks = [];
    private long _sequence = 10000;
    private object Field(object target, string name) => target.GetType().GetField(name, All)!.GetValue(target)!;
    private object? Property(object target, string name) => target.GetType().GetProperty(name, All)!.GetValue(target);
    private void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name, All)!.SetValue(target, value);
    private void SetField(object target, string name, object value) => target.GetType().GetField(name, All)!.SetValue(target, value);
    private object? Invoke(object target, string name, params object?[] args)
    {
        var method = target.GetType().GetMethod(name, All)!;
        return method.Invoke(target, args.Concat(method.GetParameters().Skip(args.Length).Select(_ => Type.Missing)).ToArray());
    }
    private async Task InvokeAsync(object target, string name, params object?[] args) => await (Task)Invoke(target, name, args)!;
    private void Check(bool passed, string name)
    {
        _checks.Add(new { name, passed });
        Save();
        if (!passed) throw new InvalidOperationException(name);
    }
    private void Save() => File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
    {
        baseline, checks = _checks, samples = _samples, clientAreaAnimation = SystemParameters.ClientAreaAnimation,
        pixelBudgetAt160Dip = new { previousEyeX = 2.5 * .35 * 160 / 384, previousEyeY = 1.25 * .3 * 160 / 384 }
    }, new JsonSerializerOptions { WriteIndented = true }));

    public async Task Run()
    {
        _window = (Window)Activator.CreateInstance(product.GetType("DuckDeskPet.PetWindow")!)!;
        _window.Opacity = 0; _window.Topmost = false; _window.ShowActivated = false;
        _settings = Field(_window, "_settings");
        SetProperty(_settings, "FollowCursor", true);
        _behavior = Field(_window, "_behavior");
        Invoke(_behavior, "SuspendAutomatic", true);
        Invoke(_behavior, "SetPaused", false);
        var preferences = Field(_window, "_companion");
        SetProperty(preferences, "ActiveBanterEnabled", false);
        _window.Show();
        for (int wait = 0; !(bool)Field(_window, "_assetsReady"); wait++)
        {
            if (wait > 1000) throw new TimeoutException("Assets failed to initialize: " + Property(_window, "CareStatus"));
            await Task.Delay(100);
        }
        _presenter = Field(_window, "_live2D");
        File.WriteAllText(Path.Combine(output, "startup.txt"), "Care: " + Property(_window, "CareStatus") + "\nPresentation: " + Property(_window, "PresentationWarning"));
        Check(_presenter is not null && (bool)Property(_presenter, "IsActive")!, "Actual PetWindow activates Live2D");
        Invoke(_window, "UnsubscribeRendering");
        ((DispatcherTimer)Field(_window, "_careTimer")).Stop();
        _image = (FrameworkElement)Field(_window, "PetImage");
        Check(_image.ActualWidth > 0 && _image.ActualHeight > 0 && _window.IsVisible, "Transparent live image remains laid out and visible");

        foreach (double scale in new[] { .8, 1d, 1.28 })
        {
            Invoke(_window, "ApplySize", scale, false);
            _window.UpdateLayout();
            foreach (var direction in new[] { ("left", -90d, 0d), ("right", 90d, 0d), ("up", 0d, -75d), ("down", 0d, 75d) })
                await Sample("idle-" + scale + "-" + direction.Item1, "Idle", direction.Item2, direction.Item3, expected: true);
        }

        Invoke(_window, "ApplySize", 1d, false);
        _window.UpdateLayout();
        await Sample("near-idle-right-20dip", "Idle", 20, 0, true);
        await Sample("near-idle-up-20dip", "Idle", 0, -20, true);
        await InvokeAsync(Field(_window, "_framePlayer"), "WarmWorkAsync");
        await InvokeAsync(Field(_window, "_workStage"), "WarmFireAsync");
        var careState = Property(_window, "CareState")!;
        SetProperty(careState, "IsWorking", true);
        Invoke(_behavior, "SetWorkState", true, false);
        await Sample("working-right", "WorkLoop", 90, 0, expected: !baseline);
        SetProperty(careState, "WorkSessionSeconds", 1900d);
        Invoke(_behavior, "SetWorkState", true, true);
        await Sample("busy-left", "BusyLoop", -90, 0, expected: !baseline);

        SetProperty(careState, "IsWorking", false);
        Invoke(_behavior, "SetWorkState", false, false);
        for (int i = 0; i < 400; i++) Invoke(_behavior, "Advance", .05d);
        SetProperty(_settings, "FollowCursor", false);
        await Sample("toggle-disabled", "Idle", 90, 0, false);
        SetProperty(_settings, "FollowCursor", true);
        SetField(_window, "_nativeDragInProgress", true);
        await Sample("drag-disabled", "Idle", 90, 0, false);
        SetField(_window, "_nativeDragInProgress", false);
        Invoke(_behavior, "SetPaused", true);
        await Sample("paused-disabled", "Idle", 90, 0, false);
        Invoke(_behavior, "SetPaused", false);
        SetField(_window, "_contentApplying", true);
        await Sample("equipment-disabled", "Idle", 90, 0, false);
        SetField(_window, "_contentApplying", false);
        var menu = (ContextMenu)Field(_window, "PetMenu");
        menu.Opacity = 0;
        menu.PlacementTarget = _image;
        menu.IsOpen = true;
        await Sample("menu-disabled", "Idle", 90, 0, false);
        menu.IsOpen = false;
        var loafing = Field(_window, "_loafingVisual");
        var loafingKind = _behavior.GetType().Assembly.GetType("DuckDeskPet.Core.LoafingKind")!;
        Invoke(loafing, "Advance", .1d, Enum.Parse(loafingKind, "NosePicking"), true, false);
        await Sample("loafing-disabled", "Idle", 90, 0, false);
        for (int i = 0; i < 80; i++) Invoke(loafing, "Advance", .1d, Enum.Parse(loafingKind, "None"), true, false);
        await Sample("restored-left", "Idle", -90, 0, true);
        await Sample("outside-radius", "Idle", 350, 0, false);
        Save();
    }

    private object MakeSample(string kind)
    {
        var core = _behavior.GetType().Assembly;
        return Activator.CreateInstance(core.GetType("DuckDeskPet.Core.ClipSample")!,
            Enum.Parse(core.GetType("DuckDeskPet.Core.ClipKind")!, kind),
            Enum.Parse(core.GetType("DuckDeskPet.Core.ClipPlaybackPhase")!, kind == "Idle" ? "Idle" : "Playing"),
            kind == "Idle" ? 0d : .25d, ++_sequence)!;
    }

    private async Task Sample(string name, string kind, double offsetX, double offsetY, bool expected)
    {
        var clip = MakeSample(kind);
        JsonElement report = default;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 1.5)
        {
            if (!GetCursorPos(out var cursor)) throw new InvalidOperationException("GetCursorPos failed");
            double contain = Math.Min(_image.ActualWidth / 384, _image.ActualHeight / 346);
            Point anchor = new((_image.ActualWidth - 384 * contain) / 2 + 193 * contain,
                (_image.ActualHeight - 346 * contain) / 2 + 147 * contain);
            Point desired = _image.PointToScreen(new Point(anchor.X + offsetX, anchor.Y + offsetY));
            var dpi = VisualTreeHelper.GetDpi(_window!);
            _window!.Left += (cursor.X - desired.X) / dpi.DpiScaleX;
            _window.Top += (cursor.Y - desired.Y) / dpi.DpiScaleY;
            Invoke(_window, "ApplyPresentation", clip, 1d / 60, false);
            foreach (Window owned in _window.OwnedWindows) owned.Opacity = 0;
            await Task.Delay(17);
        }
        string serialized = await (Task<string>)Invoke(_presenter, "GetStageDiagnosticsAsync")!;
        using var document = JsonDocument.Parse(serialized); report = document.RootElement.Clone();
        var gaze = report.GetProperty("gaze");
        var lastDpi = VisualTreeHelper.GetDpi(_window!);
        GetCursorPos(out var position);
        var local = _image.PointFromScreen(new Point(position.X, position.Y));
        _samples.Add(new
        {
            name, kind, expected, offsetX, offsetY, actualWidth = _image.ActualWidth, actualHeight = _image.ActualHeight,
            dpiX = lastDpi.DpiScaleX, dpiY = lastDpi.DpiScaleY, localX = local.X, localY = local.Y,
            windowVisible = _window!.IsVisible, imageOpacity = _image.Opacity, report,
            gates = new { followCursor = Property(_settings, "FollowCursor"), interactionUnavailable = Property(_window, "InteractionsUnavailable"),
                paused = Property(_behavior, "IsPaused"), nativeDrag = Field(_window, "_nativeDragInProgress"),
                menuOpen = ((ContextMenu)Field(_window, "PetMenu")).IsOpen }
        });
        bool enabled = gaze.GetProperty("input").GetProperty("enabled").GetBoolean();
        Check(enabled == expected, name + " real host sample enabled=" + expected);
        if (expected)
        {
            double x = gaze.GetProperty("headX").GetDouble(), y = gaze.GetProperty("headY").GetDouble();
            Check(offsetX == 0 || Math.Sign(x) == Math.Sign(offsetX) && Math.Abs(x) > .1, name + " horizontal head follows real screen cursor");
            Check(offsetY == 0 || Math.Sign(y) == -Math.Sign(offsetY) && Math.Abs(y) > .1, name + " vertical head follows real screen cursor");
        }
        await InvokeAsync(_presenter, "CaptureStageAsync", Path.Combine(output, name + ".png"));
        Save();
    }

    public void Close() { _window?.Close(); }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
}
