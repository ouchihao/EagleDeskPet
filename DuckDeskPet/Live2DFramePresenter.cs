using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using DuckDeskPet.Core;
using Microsoft.Web.WebView2.Core;

namespace DuckDeskPet;

internal sealed partial class Live2DFramePresenter : IPetPresentationBackend
{
    private const string Origin = "https://stage.eagledeskpet.invalid";
    private static long _nextGeneration;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly GraphicsSafeWebView2CompositionControl _view = new()
    {
        DefaultBackgroundColor = System.Drawing.Color.Transparent,
        IsHitTestVisible = false,
        Focusable = false,
    };
    private readonly Grid _layer = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly Grid _coreSurface = new() { IsHitTestVisible = false };
    private readonly PresentationAcknowledgementGate _acknowledgements = new(Interlocked.Increment(ref _nextGeneration));
    private readonly TaskCompletionSource<JsonElement> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Grid _parent;
    private TaskCompletionSource? _prepared, _presented;
    private PresentationEquipment? _pendingEquipment;
    private long _requestId, _revision, _pendingRevision, _gazeRevision;
    private bool _loaded, _disposed, _failed, _browserDisposed;
    private double _lastSentAt = double.NegativeInfinity, _terminalSentAt;
    private ClipSample _lastSent = ClipSample.Idle;
    private bool _lastPaused;
    private string _stageConfiguration = "";
    private string Page => Origin + "/stage/index.html?generation=" + _acknowledgements.Generation;

    internal Live2DFramePresenter(Grid parent)
    {
        _parent = parent;
        _view.GraphicsInitializationFailed += exception => Fail(exception.Message);
        Panel.SetZIndex(_layer, 10);
        _coreSurface.Children.Add(_view);
        _layer.Children.Add(_coreSurface);
        InitializeHybridLayer();
        parent.Children.Add(_layer);
    }

    public bool IsActive { get; private set; }
    public bool IsPreparing { get; private set; }
    public string? Warning { get; private set; }
    public PresentationEquipment? ActiveEquipment { get; private set; }
    internal long PresentedTerminalCount { get; private set; }
    public event Action? PresentationChanged;

    internal async Task InitializeAsync(Live2DLocalRuntime runtime, PresentationEquipment equipment, ClipSample sample, bool paused)
    {
        try
        {
            await _view.CompositionReady.WaitAsync(TimeSpan.FromSeconds(30));
            if (_disposed || _failed) return;
            PrepareFiles(runtime);
            string profile = Path.Combine(AppPaths.DataDirectory, "live2d-web-profile");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            if (_disposed || _failed) return;
            await _view.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30));
            if (_disposed || _failed) return;
            var core = _view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.NavigationStarting += (_, e) => e.Cancel = e.Uri != Page;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                try { Respond(environment, e); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    e.Response = environment.CreateWebResourceResponse(null, 500, "Local resource unavailable", "");
                    Fail("本地渲染资源读取失败：" + ex.Message);
                }
            };
            core.WebMessageReceived += OnMessage;
            core.ProcessFailed += (_, e) => Fail("渲染进程中断：" + e.ProcessFailedKind);
            core.Navigate(Page);
            var ready = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(35));
            ValidateCapabilities(ready.GetProperty("capabilities"));
            _loaded = true;
            await SelectEquipmentAsync(equipment, sample, paused);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(ex.Message); }
    }

    internal async Task SelectEquipmentAsync(PresentationEquipment equipment, ClipSample sample, bool paused)
    {
        if (!_loaded || _failed || _disposed || (IsActive && ActiveEquipment == equipment)) return;
        IsPreparing = true;
        _pendingEquipment = equipment;
        _prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _presented = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var legacy = await PrepareLegacyActionsAsync(equipment.Outfit);
            Post(new { type = "prepare", protocol = 1, generation = _acknowledgements.Generation, requestId = ++_requestId, equipment });
            await _prepared.Task.WaitAsync(TimeSpan.FromSeconds(12));
            _legacyActions = legacy;
            _pendingRevision = _revision + 1;
            SendState(sample, paused, equipment);
            await _presented.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (_failed || _disposed) return;
            ActiveEquipment = equipment;
            IsActive = true;
            _layer.Opacity = 1;
            PresentationChanged?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail("整套装备尚未完成显示：" + ex.Message); }
        finally { IsPreparing = false; _pendingEquipment = null; _prepared = _presented = null; }
    }

    public bool CanAdvance(ClipSample sample)
    {
        if (IsPreparing) return false;
        if (IsActive && UsesNativeAction(sample.Kind))
        {
            try { return CanAdvanceHybrid(sample); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(ex.Message); return true; }
        }
        if (!IsActive || _acknowledgements.CanLeave(sample)) return true;
        if (_clock.Elapsed.TotalSeconds - _terminalSentAt > 2)
            Fail("动作末帧未获得显示确认，已切回相同装备的逐帧动画");
        return false;
    }

    public void Present(ClipSample sample, bool paused, bool force = false)
    {
        if (!IsActive || IsPreparing || ActiveEquipment is null) return;
        try { if (PresentHybrid(sample, paused)) return; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(ex.Message); return; }
        if (force || sample.Kind != _lastSent.Kind || sample.Sequence != _lastSent.Sequence ||
            sample.Phase != _lastSent.Phase || paused != _lastPaused || _clock.Elapsed.TotalSeconds - _lastSentAt >= 0.1)
        {
            try { SendState(sample, paused, ActiveEquipment); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(ex.Message); }
        }
    }

    private void SendState(ClipSample sample, bool paused, PresentationEquipment equipment)
    {
        if (sample.Phase == ClipPlaybackPhase.Terminal &&
            (_lastSent.Phase != ClipPlaybackPhase.Terminal || _lastSent.Sequence != sample.Sequence || _lastSent.Kind != sample.Kind))
            _terminalSentAt = _clock.Elapsed.TotalSeconds;
        _acknowledgements.Sent(sample);
        _lastSent = sample;
        _lastPaused = paused;
        _lastSentAt = _clock.Elapsed.TotalSeconds;
        Post(new { type = "state", protocol = 1, generation = _acknowledgements.Generation, revision = ++_revision,
            sequence = sample.Sequence, kind = sample.Kind.ToString(), phase = sample.Phase.ToString(),
            progress = sample.Progress, durationSeconds = sample.DurationSeconds, paused, equipment });
    }

    internal void SendGaze(GazeTarget target)
    {
        if (!IsActive || IsPreparing || _disposed || _failed) return;
        try
        {
            Post(new { type = "gaze", protocol = 1, generation = _acknowledgements.Generation,
                revision = ++_gazeRevision, enabled = target.Enabled, x = target.X, y = target.Y });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(ex.Message); }
    }

    private void Post(object message)
    {
        if (_disposed || _failed) throw new InvalidOperationException("Live2D 渲染已退出");
        _view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || _failed || e.Source != Page || e.WebMessageAsJson.Length > 64 * 1024) return;
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var message = json.RootElement;
            if (message.GetProperty("protocol").GetInt32() != 1 ||
                message.GetProperty("generation").GetInt64() != _acknowledgements.Generation) return;
            switch (message.GetProperty("type").GetString())
            {
                case "ready": _ready.TrySetResult(message.Clone()); break;
                case "prepared":
                    if (message.GetProperty("requestId").GetInt64() == _requestId && SameEquipment(message, _pendingEquipment))
                        _prepared?.TrySetResult();
                    break;
                case "prepare-error":
                    if (message.GetProperty("requestId").GetInt64() == _requestId)
                        _prepared?.TrySetException(new InvalidOperationException(message.GetProperty("message").GetString()));
                    break;
                case "presented":
                    if (message.GetProperty("revision").GetInt64() == _pendingRevision &&
                        message.GetProperty("sequence").GetInt64() == _lastSent.Sequence &&
                        message.GetProperty("kind").GetString() == _lastSent.Kind.ToString() && SameEquipment(message, _pendingEquipment))
                        _presented?.TrySetResult();
                    break;
                case "terminal":
                    if (Enum.TryParse<ClipKind>(message.GetProperty("kind").GetString(), out var kind) && !UsesNativeAction(kind) &&
                        _acknowledgements.Acknowledge(_acknowledgements.Generation, message.GetProperty("sequence").GetInt64(), kind))
                        PresentedTerminalCount++;
                    break;
                case "neutralized": ReceiveNeutralized(message); break;
                case "error": Fail(message.GetProperty("message").GetString() ?? "Live2D 画面异常"); break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
    }

    private static bool SameEquipment(JsonElement message, PresentationEquipment? equipment)
    {
        if (equipment is null) return false;
        var actual = message.GetProperty("equipment");
        return actual.GetProperty("desk").GetString() == equipment.Desk &&
            actual.GetProperty("computer").GetString() == equipment.Computer && actual.GetProperty("outfit").GetString() == equipment.Outfit;
    }

    private static void ValidateCapabilities(JsonElement capabilities)
    {
        if (capabilities.GetProperty("backend").GetString() != "live2d-core") throw new InvalidDataException("不是官方 Core 模型渲染器");
        void Require(string key, IEnumerable<string> expected)
        {
            var actual = capabilities.GetProperty(key).EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal);
            if (!expected.All(actual.Contains)) throw new InvalidDataException("Live2D 尚缺少完整资源组：" + key);
        }
        Require("clips", AnimationAssets.Load().Actions.Select(x => x.Clip).Append("Idle"));
        Require("desks", ContentCatalog.Definitions.Where(x => x.Type == ContentType.Desk).Select(x => x.ScenePropId!));
        Require("computers", ContentCatalog.Definitions.Where(x => x.Type == ContentType.Computer).Select(x => x.ScenePropId!));
        Require("outfits", OutfitCatalog.Load().Outfits.Select(x => x.Id));
    }

    private void PrepareFiles(Live2DLocalRuntime runtime)
    {
        string manifestPath = Path.Combine(Path.GetDirectoryName(runtime.Model)!, "runtime-manifest.json");
        var configuration = File.Exists(manifestPath)
            ? JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject() ?? throw new InvalidDataException("Live2D runtime manifest is empty")
            : new JsonObject();
        configuration["modelUrl"] = "/model/eagle.model3.json";
        configuration["sceneCatalogUrl"] = "/assets/SceneProps/work-scenes.json";
        configuration["assetRoot"] = "/assets/";
        _stageConfiguration = configuration.ToJsonString();
        void AddTree(string root, string prefix, Func<string, bool> include)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!include(file)) continue;
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || new FileInfo(file).Length > 128 * 1024 * 1024)
                    throw new InvalidDataException("Live2D 资源不允许链接或超大文件");
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                _files[prefix + relative] = file;
            }
        }
        AddTree(Path.Combine(AppContext.BaseDirectory, "Live2DStage"), "/stage/", _ => true);
        _files["/runtime/core.js"] = Path.Combine(runtime.SdkRoot, "Core", "live2dcubismcore.js");
        _files["/runtime/framework.js"] = runtime.Framework;
        AddTree(Path.Combine(runtime.SdkRoot, "Framework", "Shaders", "WebGL"), "/runtime/shaders/", _ => true);
        AddTree(Path.GetDirectoryName(runtime.Model)!, "/model/", file => Path.GetExtension(file) is ".json" or ".png" or ".moc3");
        _files["/model/eagle.model3.json"] = runtime.Model;
        foreach (string group in new[] { "SceneProps", "Outfits" })
            AddTree(Path.Combine(runtime.AssetsRoot, group), "/assets/" + group + "/", file =>
                Path.GetExtension(file) == ".json" || (Path.GetExtension(file) == ".png" && !file.Contains(Path.DirectorySeparatorChar + "Animations" + Path.DirectorySeparatorChar)));
    }

    private void Respond(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (e.Request.Method != "GET" || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) ||
            uri.GetLeftPart(UriPartial.Authority) != Origin)
        { e.Response = environment.CreateWebResourceResponse(null, 403, "Local stage only", ""); return; }
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path == "/stage/config.json")
        {
            e.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(_stageConfiguration)), 200, "OK", "Content-Type: application/json\r\nCache-Control: no-store");
            return;
        }
        if (!_files.TryGetValue(path, out string? file))
        { e.Response = environment.CreateWebResourceResponse(null, 404, "Not mapped", ""); return; }
        string mime = Path.GetExtension(file).ToLowerInvariant() switch
        { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".json" => "application/json", ".png" => "image/png", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
        e.Response = environment.CreateWebResourceResponse(File.OpenRead(file), 200, "OK",
            "Content-Type: " + mime + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
    }

    private void Fail(string reason)
    {
        if (_disposed || _failed) return;
        _failed = true;
        IsActive = false;
        IsPreparing = false;
        Warning = "Live2D 暂不可用，已保留相同装备并切回逐帧动画。" + reason;
        _view.Visibility = Visibility.Collapsed;
        _layer.Visibility = Visibility.Collapsed;
        _ready.TrySetCanceled();
        _prepared?.TrySetCanceled();
        _presented?.TrySetCanceled();
        PresentationChanged?.Invoke();
        _parent.Dispatcher.BeginInvoke(new Action(ReleaseBrowser));
    }

    internal async Task CaptureStageAsync(string path)
    {
        if (!IsActive) throw new InvalidOperationException("Live2D is not active");
        if (IsNativeRasterVisible) { await CaptureNativeRasterAsync(path); return; }
        string result = await _view.CoreWebView2.ExecuteScriptAsync("window.eagleStage.capture()");
        string value = JsonSerializer.Deserialize<string>(result) ?? throw new InvalidDataException("Canvas capture returned no image");
        const string prefix = "data:image/png;base64,";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected canvas capture format");
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(value[prefix.Length..]));
    }

    internal async Task<string> GetStageDiagnosticsAsync()
    {
        if (!IsActive) throw new InvalidOperationException("Live2D is not active");
        var report = JsonNode.Parse(await _view.CoreWebView2.ExecuteScriptAsync("window.eagleStage.report()"))!.AsObject();
        report["visibleSurface"] = VisibleSurface;
        report["nativeRasterTerminalCount"] = NativeRasterTerminalCount;
        report["nativeRasterFrames"] = _nativeFrameChanges;
        report["activeOutfitActionRouting"] = JsonSerializer.SerializeToNode(new
        {
            outfit = ActiveEquipment?.Outfit,
            Yawn = UsesNativeAction(ClipKind.Yawn) ? "native-raster" : "live2d-core",
            Eat = UsesNativeAction(ClipKind.Eat) ? "native-raster" : "live2d-core",
        });
        report["handoffPhase"] = _hybridPhase.ToString();
        return report.ToJsonString();
    }

    internal async Task BeginStageRecordingAsync()
    {
        if (!IsActive) throw new InvalidOperationException("Live2D is not active");
        await PrepareVisibleRecordingAsync();
        await _view.CoreWebView2.ExecuteScriptAsync("window.eagleStage.beginRecording()");
    }

    internal async Task FinishStageRecordingAsync(string path)
    {
        if (!IsActive) throw new InvalidOperationException("Live2D is not active");
        await _view.CoreWebView2.ExecuteScriptAsync("window.__eagleRecordingResult=null; window.eagleStage.finishRecording().then(data=>{window.__eagleRecordingResult=data.length<=44739244?{data}:{error:'Recording exceeds 32 MiB'}}).catch(error=>{window.__eagleRecordingResult={error:String(error)}})");
        for (int attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(100);
            using var result = JsonDocument.Parse(await _view.CoreWebView2.ExecuteScriptAsync("window.__eagleRecordingResult"));
            if (result.RootElement.ValueKind == JsonValueKind.Null) continue;
            if (result.RootElement.TryGetProperty("error", out var error)) throw new InvalidDataException(error.GetString());
            byte[] bytes = Convert.FromBase64String(result.RootElement.GetProperty("data").GetString()!);
            if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("Recording exceeds 32 MiB");
            await File.WriteAllBytesAsync(path, bytes);
            _recordingVisibleSurface = false;
            await _view.CoreWebView2.ExecuteScriptAsync("delete window.__eagleRecordingResult");
            return;
        }
        throw new TimeoutException("Live2D canvas recorder did not finish");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsActive = false;
        IsPreparing = false;
        _ready.TrySetCanceled();
        _prepared?.TrySetCanceled();
        _presented?.TrySetCanceled();
        ReleaseBrowser();
    }

    private void ReleaseBrowser()
    {
        if (_browserDisposed) return;
        _browserDisposed = true;
        _view.StopGraphicsInitialization();
        DisposeHybridLayer();
        _parent.Children.Remove(_layer);
        _view.Dispose();
    }
}
