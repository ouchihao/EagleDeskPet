using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is not (5 or 6))
        {
            Console.Error.WriteLine("GazeStageSelfTest <repository> <official-sdk-root> <framework.js> <model-pack-root> <new-output> [default-candidate-directory]");
            return 2;
        }
        try { new Application().Run(new GazeWindow(args.Select(Path.GetFullPath).ToArray())); return Environment.ExitCode; }
        catch (Exception error) { Console.Error.WriteLine(error); return 2; }
    }
}

internal sealed class GazeWindow : Window
{
    private const string Origin = "https://stage.eagledeskpet.invalid";
    private const string Page = Origin + "/stage/index.html?generation=1&qa=1";
    private readonly WebView2 _view = new();
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly List<JsonElement> _messages = [];
    private readonly string[] _args;
    private readonly string _output;
    private string _configuration = "";

    internal GazeWindow(string[] args)
    {
        _args = args; _output = args[4];
        if (Directory.Exists(_output) && Directory.EnumerateFileSystemEntries(_output).Any()) throw new IOException("Use a new empty evidence directory");
        Directory.CreateDirectory(_output);
        Console.WriteLine("EXACT OUTPUT " + _output);
        Width = 820; Height = 780; ShowActivated = false; ShowInTaskbar = false;
        Title = "Isolated real Stage cursor-gaze verification"; Content = _view;
        Loaded += async (_, _) =>
        {
            try { await Run(); }
            catch (Exception error) { File.WriteAllText(Path.Combine(_output, "failure.txt"), error.ToString()); Environment.ExitCode = 1; }
            finally { _view.Dispose(); Close(); }
        };
    }

    private void MapTree(string root, string prefix, string[] extensions)
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(file))) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked QA resource rejected");
            _files[prefix + Path.GetRelativePath(root, file).Replace('\\', '/')] = file;
        }
    }

    private void Prepare()
    {
        string repo = _args[0], sdk = _args[1], model = _args[3];
        if (!File.ReadAllText(Path.Combine(sdk, "cubism-info.yml")).Contains("version: 5-r.5", StringComparison.Ordinal)) throw new IOException("Official Web SDK 5-r.5 required");
        MapTree(Path.Combine(repo, "DuckDeskPet", "Live2DStage"), "/stage/", [".js", ".html", ".css"]);
        MapTree(Path.Combine(repo, "DuckDeskPet", "Assets", "SceneProps"), "/assets/SceneProps/", [".json", ".png"]);
        MapTree(Path.Combine(sdk, "Framework", "Shaders", "WebGL"), "/runtime/shaders/", [".frag", ".vert"]);
        MapTree(model, "/model/", [".json", ".png", ".moc3"]);
        _files["/runtime/core.js"] = Path.Combine(sdk, "Core", "live2dcubismcore.js");
        _files["/runtime/framework.js"] = _args[2];
        JsonNode config = JsonNode.Parse(File.ReadAllText(Path.Combine(model, "runtime-manifest.json")))!;
        config["modelUrl"] = "/model/eagle.model3.json";
        config["sceneCatalogUrl"] = "/assets/SceneProps/work-scenes.json";
        config["assetRoot"] = "/assets/";
        if (_args.Length == 6)
        {
            MapTree(_args[5], "/candidate/", [".json", ".png", ".moc3"]);
            config["modelUrl"] = "/candidate/eagle.model3.json";
            config["outfits"]!["outfit.default"]!["modelUrl"] = "/candidate/eagle.model3.json";
        }
        _configuration = config.ToJsonString();
        var provenance = _files.Where(p => p.Key.StartsWith("/stage/") || p.Key.EndsWith(".moc3") || p.Key.StartsWith("/runtime/") && p.Key.EndsWith(".js"))
            .Select(p => new { url = p.Key, source = p.Value, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Value))).ToLowerInvariant() });
        File.WriteAllText(Path.Combine(_output, "provenance.json"), JsonSerializer.Serialize(provenance, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task Run()
    {
        Prepare();
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(_output, "isolated-profile"));
        await _view.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30));
        var core = _view.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NavigationStarting += (_, e) => e.Cancel = e.Uri != Page;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            var uri = new Uri(e.Request.Uri); string path = uri.AbsolutePath;
            byte[]? bytes = null;
            if (uri.GetLeftPart(UriPartial.Authority) == Origin && e.Request.Method == "GET")
            {
                if (path == "/stage/config.json") bytes = Encoding.UTF8.GetBytes(_configuration);
                else if (_files.TryGetValue(path, out string? file)) bytes = File.ReadAllBytes(file);
            }
            string mime = Path.GetExtension(path) switch { ".js" => "text/javascript", ".json" => "application/json", ".html" => "text/html", ".css" => "text/css", ".png" => "image/png", _ => "application/octet-stream" };
            e.Response = environment.CreateWebResourceResponse(new MemoryStream(bytes ?? []), bytes is null ? 403 : 200, bytes is null ? "Local inputs only" : "OK", "Content-Type: " + mime + "\r\nCache-Control: no-store");
        };
        core.WebMessageReceived += (_, e) =>
        {
            if (e.Source != Page || e.WebMessageAsJson.Length > 4 * 1024 * 1024) return;
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement message = document.RootElement;
            if (message.GetProperty("type").GetString() == "gaze-qa-frame")
            {
                string name = message.GetProperty("name").GetString()!;
                if (name.Length > 80 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw new IOException("Invalid evidence name");
                string data = message.GetProperty("data").GetString()!;
                if (!data.StartsWith("data:image/png;base64,", StringComparison.Ordinal)) throw new IOException("Expected canvas PNG");
                File.WriteAllBytes(Path.Combine(_output, name + ".png"), Convert.FromBase64String(data[22..]));
            }
            else _messages.Add(message.Clone());
        };
        core.Navigate(Page);
        for (int i = 0; await core.ExecuteScriptAsync("!!window.eagleStage") != "true"; i++)
        {
            if (i >= 400) throw new TimeoutException("Stage unavailable: " + JsonSerializer.Serialize(_messages));
            await Task.Delay(100);
        }
        await core.ExecuteScriptAsync(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenario.js")));
        JsonElement result = default;
        for (int i = 0; i < 900; i++)
        {
            await Task.Delay(100);
            using var document = JsonDocument.Parse(await core.ExecuteScriptAsync("window.gazeQaResult ?? null"));
            if (document.RootElement.ValueKind == JsonValueKind.Null) continue;
            result = document.RootElement.Clone(); break;
        }
        if (result.ValueKind == JsonValueKind.Undefined) throw new TimeoutException("Gaze scenario timed out");
        if (result.TryGetProperty("video", out var video))
        {
            File.WriteAllBytes(Path.Combine(_output, "stage-gaze.webm"), Convert.FromBase64String(video.GetString()!));
            JsonNode compact = JsonNode.Parse(result.GetRawText())!; compact.AsObject().Remove("video");
            File.WriteAllText(Path.Combine(_output, "report.json"), compact.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        else File.WriteAllText(Path.Combine(_output, "report.json"), result.GetRawText());
        File.WriteAllText(Path.Combine(_output, "messages.json"), JsonSerializer.Serialize(_messages, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = result.TryGetProperty("passed", out var passed) && passed.GetBoolean() ? 0 : 1;
    }
}
