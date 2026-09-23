using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Live2DModelProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ProbeOptions? options = null;
        try
        {
            if (args.Contains("--help")) { Console.WriteLine(ProbeOptions.Usage); return 0; }
            if (args.SequenceEqual(new[] { "--self-test" })) return ProbeSelfTest.Run();
            options = ProbeOptions.Parse(args);
            Directory.CreateDirectory(options.Output);
            Console.WriteLine("EXACT OUTPUT " + options.Output);
            var app = new Application();
            app.Run(new ProbeWindow(options));
            return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message + "\n" + ProbeOptions.Usage);
            if (options is not null) File.WriteAllText(Path.Combine(options.Output, "failure.txt"), ex.ToString());
            return 2;
        }
    }
}

internal sealed record ProbeOptions(string Sdk, string Model, string Output, string Framework, int Seconds, bool Preview, bool EagleContract)
{
    internal const string Usage = "Live2DModelProbe --sdk <absolute official Web R5 SDK root> --framework <absolute local Framework.js> --model <absolute model3.json> --output <new absolute directory> [--seconds 10] [--preview] [--eagle-contract]";
    internal static ProbeOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool preview = false, eagleContract = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--preview") { preview = true; continue; }
            if (args[i] == "--eagle-contract") { eagleContract = true; continue; }
            if (args[i] is not ("--sdk" or "--model" or "--output" or "--framework" or "--seconds") || ++i >= args.Length || !values.TryAdd(args[i - 1], args[i]))
                throw new ArgumentException("Unknown, duplicated or incomplete argument");
        }
        string Required(string key)
        {
            if (!values.TryGetValue(key, out string? value) || !Path.IsPathFullyQualified(value)) throw new ArgumentException(key + " requires an absolute path");
            return Path.GetFullPath(value);
        }
        string sdk = Required("--sdk"), model = Required("--model"), output = Required("--output");
        if (!File.Exists(Path.Combine(sdk, "Core", "live2dcubismcore.js")) || !File.Exists(Path.Combine(sdk, "cubism-info.yml")))
            throw new ArgumentException("SDK must be the extracted official CubismSdkForWeb-5-r.5 root");
        if (!File.ReadAllText(Path.Combine(sdk, "cubism-info.yml")).Contains("version: 5-r.5", StringComparison.Ordinal))
            throw new ArgumentException("This probe is pinned to official SDK Web 5-r.5");
        if (!model.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase) || !File.Exists(model)) throw new ArgumentException("Missing model3.json");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("Output must be empty; evidence is never overwritten");
        if (output.StartsWith(sdk + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(Path.GetDirectoryName(model)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output cannot be inside the SDK or model inputs");
        string framework = Required("--framework");
        if (!File.Exists(framework) || !File.Exists(framework + ".provenance.json")) throw new ArgumentException("Build the local Framework bundle first");
        VerifyFramework(sdk, framework);
        int seconds = 10;
        if (values.TryGetValue("--seconds", out string? text) && !int.TryParse(text, out seconds)) throw new ArgumentException("Sampling seconds must be an integer");
        if (seconds is < 2 or > 60) throw new ArgumentException("Sampling seconds must be 2–60");
        return new(sdk, model, output, framework, seconds, preview, eagleContract);
    }

    internal static void VerifyFramework(string sdk, string framework)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(framework + ".provenance.json"));
        var provenance = json.RootElement;
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        if (provenance.GetProperty("sdkRelease").GetString() != "5-r.5" ||
            provenance.GetProperty("sdkInfo").GetString() != File.ReadAllText(Path.Combine(sdk, "cubism-info.yml")) ||
            provenance.GetProperty("bundleSha256").GetString() != Hash(framework))
            throw new InvalidDataException("Framework bundle hash or SDK provenance mismatch; rebuild from this SDK");
        foreach (var input in provenance.GetProperty("inputs").EnumerateArray())
        {
            string name = input.GetProperty("name").GetString()!;
            if (name.Contains(':') || name.Contains('\\') || name.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("Framework provenance input escapes SDK");
            string path = Path.GetFullPath(Path.Combine(sdk, name));
            if (!path.StartsWith(sdk + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || input.GetProperty("sha256").GetString() != Hash(path))
                throw new InvalidDataException("Framework source hash mismatch; rebuild from this SDK");
        }
    }
}

internal sealed class ProbeWindow : Window
{
    private const string Origin = "https://live2d-probe.invalid/";
    private readonly ProbeOptions _options;
    private readonly WebView2 _view = new();
    private readonly TaskCompletionSource<JsonElement> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly List<string> _blocked = [];
    private string _configuration = "";

    internal ProbeWindow(ProbeOptions options)
    {
        _options = options;
        Title = "Official Cubism Core R5 · isolated model/WebGL probe";
        Width = 820; Height = 850; ShowActivated = options.Preview; ShowInTaskbar = options.Preview;
        Content = _view;
        Loaded += Run;
        Closed += (_, _) => _view.Dispose();
    }

    private void PrepareInputMap()
    {
        string stage = Path.Combine(AppContext.BaseDirectory, "Stage");
        foreach (string name in new[] { "index.html", "probe.js", "probe.css", "audit.js" }) _files[Origin + name] = Path.Combine(stage, name);
        string core = Path.Combine(_options.Sdk, "Core", "live2dcubismcore.js");
        _files[Origin + "sdk/core.js"] = core;
        _files[Origin + "sdk/framework.js"] = _options.Framework;
        string shaders = Path.Combine(_options.Sdk, "Framework", "Shaders", "WebGL");
        foreach (string file in Directory.EnumerateFiles(shaders, "*", SearchOption.AllDirectories))
            _files[Origin + "sdk/shaders/" + Path.GetRelativePath(shaders, file).Replace('\\', '/')] = file;
        using var json = JsonDocument.Parse(File.ReadAllText(_options.Model));
        var references = json.RootElement.GetProperty("FileReferences");
        string directory = Path.GetDirectoryName(_options.Model)!;
        string Map(string relative)
        {
            if (relative.Length is < 1 or > 250 || relative.Contains(':') || relative.Contains('\\') || relative.StartsWith('/') ||
                relative.Split('/').Any(part => part is ".." or "." or "")) throw new InvalidDataException("Model references must be bounded relative local paths");
            string full = Path.GetFullPath(Path.Combine(directory, relative));
            if (!full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new InvalidDataException("Model reference is missing or escapes its directory");
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 || new FileInfo(full).Length > 128 * 1024 * 1024)
                throw new InvalidDataException("Model resource is a link or exceeds the 128 MiB probe limit");
            string url = Origin + "model/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
            _files[url] = full;
            return url;
        }
        var textures = references.GetProperty("Textures").EnumerateArray().Select(item => Map(item.GetString()!)).ToArray();
        if (textures.Length is < 1 or > 16) throw new InvalidDataException("Expected 1–16 textures");
        string mocUrl = Map(references.GetProperty("Moc").GetString()!);
        object Hash(string file) => new { name = Path.GetFileName(file), bytes = new FileInfo(file).Length,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant() };
        var provenance = new
        {
            sdkRelease = "Cubism SDK for Web 5-r.5", core = Hash(core), framework = Hash(_options.Framework), manifest = Hash(_options.Model),
            moc = Hash(_files[mocUrl]), textures = textures.Select(url => Hash(_files[url])).ToArray(),
            sdkInfo = File.ReadAllText(Path.Combine(_options.Sdk, "cubism-info.yml")),
            createdUtc = DateTimeOffset.UtcNow,
            inputPolicy = "User-provided local SDK; no official Core copied, patched, downloaded by this tool, or embedded in repository"
        };
        File.WriteAllText(Path.Combine(_options.Output, "provenance.json"), JsonSerializer.Serialize(provenance, new JsonSerializerOptions { WriteIndented = true }));
        _configuration = JsonSerializer.Serialize(new { name = Path.GetFileNameWithoutExtension(_options.Model), mocUrl, textures, eagleContract = _options.EagleContract, sampleSeconds = _options.Seconds });
    }

    private async void Run(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            PrepareInputMap();
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(_options.Output, "isolated-web-profile"));
            await _view.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30));
            var core = _view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = _options.Preview;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.NavigationStarting += (_, e) => e.Cancel = e.Uri != Origin + "index.html";
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                string uri = e.Request.Uri;
                if (e.Request.Method != "GET") { e.Response = environment.CreateWebResourceResponse(null, 405, "Method not allowed", ""); return; }
                if (uri == Origin + "config.json")
                {
                    e.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(_configuration)), 200, "OK", "Content-Type: application/json\r\nCache-Control: no-store");
                    return;
                }
                if (!_files.TryGetValue(uri, out string? path))
                {
                    if (_blocked.Count < 50) _blocked.Add(uri.Length > 256 ? uri[..256] : uri);
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Only explicit local inputs allowed", "");
                    return;
                }
                string mime = Path.GetExtension(path).ToLowerInvariant() switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".png" => "image/png", ".json" => "application/json", _ => "application/octet-stream" };
                e.Response = environment.CreateWebResourceResponse(File.OpenRead(path), 200, "OK", "Content-Type: " + mime + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
            };
            core.ProcessFailed += (_, e) => _ready.TrySetException(new InvalidOperationException("WebView process failed: " + e.ProcessFailedKind));
            core.WebMessageReceived += (_, e) =>
            {
                if (e.Source != Origin + "index.html" || e.WebMessageAsJson.Length > 1024 * 1024) return;
                try
                {
                    using var message = JsonDocument.Parse(e.WebMessageAsJson);
                    if (message.RootElement.GetProperty("type").GetString() == "model-probe-ready") _ready.TrySetResult(message.RootElement.Clone());
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
            };
            core.Navigate(Origin + "index.html");
            JsonElement initial = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(40));
            File.WriteAllText(Path.Combine(_options.Output, "initial.json"), await core.ExecuteScriptAsync("window.probe.report()"));
            if (!initial.GetProperty("loaded").GetBoolean()) throw new InvalidDataException(initial.GetProperty("error").GetString());
            foreach (string pose in new[] { "neutral", "eyes-closed", "eyes-half", "breath-high", "head-left", "head-right", "mouth-open" })
            {
                await core.ExecuteScriptAsync("window.probe.pose(" + JsonSerializer.Serialize(pose) + ")");
                await Task.Delay(150);
                using var capture = File.Create(Path.Combine(_options.Output, pose + ".png"));
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
            }
            await core.ExecuteScriptAsync("window.probe.resetMetrics(); window.probe.play(true); window.probe.beginRecording()");
            await Task.Delay(TimeSpan.FromSeconds(_options.Seconds));
            await core.ExecuteScriptAsync("window.probe.videoResult=null; window.probe.finishRecording().then(data=>{window.probe.videoResult={data}}).catch(error=>{window.probe.videoResult={error:String(error)}})");
            bool videoSaved = false;
            for (int attempt = 0; attempt < 50; attempt++)
            {
                await Task.Delay(100);
                using var video = JsonDocument.Parse(await core.ExecuteScriptAsync("window.probe.videoResult"));
                if (video.RootElement.ValueKind == JsonValueKind.Null) continue;
                if (video.RootElement.TryGetProperty("error", out var error)) throw new InvalidDataException(error.GetString());
                string data = video.RootElement.GetProperty("data").GetString()!;
                if (data.Length > 24 * 1024 * 1024) throw new InvalidDataException("Video output exceeds bound");
                File.WriteAllBytes(Path.Combine(_options.Output, "animation-30fps.webm"), Convert.FromBase64String(data));
                videoSaved = true;
                break;
            }
            if (!videoSaved) throw new TimeoutException("Video encoding timed out");
            string reportText = await core.ExecuteScriptAsync("window.probe.report()");
            using var report = JsonDocument.Parse(reportText);
            var result = new { browserRuntime = environment.BrowserVersionString, webViewSdk = "1.0.4191.47", blockedRequests = _blocked,
                probe = report.RootElement.Clone(), wallClockSampleSeconds = _options.Seconds,
                limitations = new[] { "Official Framework WebGL renderer with original local SDK shaders", "No physics/motion3 playback or 19-action/full-outfit acceptance", "Frame callbacks are not physical Present FPS", "No pet state, network or default browser profile used" } };
            File.WriteAllText(Path.Combine(_options.Output, "report.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = report.RootElement.GetProperty("passed").GetBoolean() ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(_options.Output, "failure.txt"), ex.ToString());
            Environment.ExitCode = 1;
        }
        finally { if (!_options.Preview) Close(); }
    }
}
