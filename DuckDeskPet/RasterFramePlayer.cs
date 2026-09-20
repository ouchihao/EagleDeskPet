using System.IO;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DuckDeskPet.Core;

namespace DuckDeskPet;

internal enum OutfitSelectionStatus { Applied, AlreadySelected, Unavailable, UnsafeBoundary, Superseded, Cancelled }

internal sealed record OutfitSelectionResult(OutfitSelectionStatus Status, string RequestedId,
    string EffectiveId, string? Warning, bool UsedFallback = false)
{
    public bool Success => Status is OutfitSelectionStatus.Applied or OutfitSelectionStatus.AlreadySelected || UsedFallback;
}

/// <summary>
/// UI-thread-owned, predecoded PNG sequences. Apply performs no I/O, blending, or outfit substitution.
/// Callers prewarm clips before scheduling them and defer outfit selection until a standing boundary.
/// </summary>
internal sealed class RasterFramePlayer : IDisposable
{
    private static readonly ClipKind[] CommonClips = [ClipKind.Yawn, ClipKind.Shy, ClipKind.Eat];
    private readonly Image _target;
    private readonly OutfitCatalog _catalog;
    private AnimationAssets _assets;
    private LayeredOutfitPack? _layers;
    private BitmapSource _neutral;
    private readonly Dictionary<ClipKind, BitmapSource[]> _frames = new();
    private readonly Dictionary<ClipKind, Task> _loads = new();
    private readonly Dictionary<ClipKind, long> _clipLoadGenerations = new();
    private readonly SemaphoreSlim _loadGate = new(2, 2);
    private readonly CancellationTokenSource _lifetime = new();
    private BitmapSource _displayed;
    private ClipKind _currentKind = ClipKind.Idle;
    private ClipKind _displayedKind = ClipKind.Idle;
    private long _selectionRequest;
    private long _outfitGeneration;
    private bool _disposed;

    public RasterFramePlayer(Image target) : this(target, OutfitCatalog.Load()) { }

    internal RasterFramePlayer(Image target, OutfitCatalog catalog)
    {
        _target = target;
        _target.Dispatcher.VerifyAccess();
        _catalog = catalog;
        _assets = catalog.DefaultAssets;
        _neutral = OutfitBitmap.Load(_catalog.Resources, _assets.Neutral);
        _target.Source = _displayed = _neutral;
        Warning = catalog.Warning;
    }

    public string CurrentOutfitId { get; private set; } = OutfitCatalog.DefaultId;
    public string? Warning { get; private set; }
    public bool IsAtSafeOutfitBoundary => _currentKind == ClipKind.Idle && !_disposed;
    internal int DecodedFrameCount => _frames.Values.Sum(x => x.Length);

    // Preserve immediate legacy interactions; all expansion clips remain on demand.
    public Task WarmAsync() => WarmClipsAsync(CommonClips);
    public Task WarmWorkAsync() => WarmClipsAsync(_assets.Actions.Select(x => Enum.Parse<ClipKind>(x.Clip)).Where(ClipCatalog.IsWorkScene));

    public Task<OutfitAvailability> GetOutfitAvailabilityAsync(string id, CancellationToken token = default) =>
        _catalog.GetAvailabilityAsync(id, token);

    public bool IsClipReady(ClipKind kind)
    {
        VerifyActive();
        return kind == ClipKind.Idle || _frames.ContainsKey(kind);
    }

    public Task WarmClipsAsync(IEnumerable<ClipKind> kinds, CancellationToken token = default)
    {
        VerifyActive();
        return Task.WhenAll(kinds.Distinct().Select(kind => WarmClipAsync(kind, token)));
    }

    public Task WarmClipAsync(ClipKind kind, CancellationToken token = default)
    {
        VerifyActive();
        token.ThrowIfCancellationRequested();
        if (kind == ClipKind.Idle || _frames.ContainsKey(kind)) return Task.CompletedTask;
        if (_loads.TryGetValue(kind, out var current)) return current.WaitAsync(token);
        var action = _assets.Actions.SingleOrDefault(x => x.Clip == kind.ToString());
        if (action is null)
        {
            Warning = $"当前服装不支持动作 {kind}；已保留当前画面。";
            return Task.FromException(new InvalidDataException(Warning));
        }
        var load = LoadClipAsync(action, kind, _outfitGeneration, ClipLoadGeneration(kind), _layers);
        _loads[kind] = load;
        return load.WaitAsync(token);
    }

    private async Task LoadClipAsync(ActionAsset action, ClipKind kind, long generation, long clipGeneration, LayeredOutfitPack? layers)
    {
        bool acquired = false;
        try
        {
            await _loadGate.WaitAsync(_lifetime.Token);
            acquired = true;
            var frames = await Task.Run(() => LoadSequence(_catalog.Resources, action, _lifetime.Token, layers), _lifetime.Token);
            if (IsCurrentLoad(kind, generation, clipGeneration)) _frames[kind] = frames;
        }
        catch (Exception ex) when (OutfitCatalog.IsResourceError(ex))
        {
            if (IsCurrentLoad(kind, generation, clipGeneration)) Warning = "动作资源加载失败，保留当前外观：" + ex.Message;
            throw;
        }
        finally
        {
            if (acquired) _loadGate.Release();
            if (IsCurrentLoad(kind, generation, clipGeneration)) _loads.Remove(kind);
        }
    }

    private long ClipLoadGeneration(ClipKind kind) => _clipLoadGenerations.GetValueOrDefault(kind);

    private bool IsCurrentLoad(ClipKind kind, long generation, long clipGeneration) => !_disposed &&
        generation == _outfitGeneration && clipGeneration == ClipLoadGeneration(kind);

    /// <summary>
    /// Release explicitly unneeded sequences at an owner-chosen boundary. Common interactions and
    /// active/displayed clips remain resident. This never schedules a reload or changes the image.
    /// Per-clip generations ensure a released decode cannot refill the cache or remove a newer load.
    /// </summary>
    public void ReleaseClips(IEnumerable<ClipKind> kinds)
    {
        VerifyActive();
        ArgumentNullException.ThrowIfNull(kinds);
        foreach (var kind in kinds.Distinct().ToArray())
        {
            if (!ClipCatalog.IsKnown(kind) || kind == ClipKind.Idle || CommonClips.Contains(kind) ||
                kind == _currentKind || kind == _displayedKind) continue;
            _clipLoadGenerations[kind] = ClipLoadGeneration(kind) + 1;
            _frames.Remove(kind);
            _loads.Remove(kind);
        }
    }

    public void ReleaseWorkFrames()
    {
        VerifyActive();
        ReleaseClips(_assets.Actions.Select(x => Enum.Parse<ClipKind>(x.Clip)).Where(ClipCatalog.IsWorkScene));
    }

    public async Task<OutfitSelectionResult> SelectOutfitAsync(string outfitId, CancellationToken token = default)
    {
        VerifyActive();
        long request = ++_selectionRequest; // Even choosing the already selected default supersedes an older request.
        if (!IsAtSafeOutfitBoundary) return Selection(OutfitSelectionStatus.UnsafeBoundary, outfitId, "请在站立时切换服装。", request);
        if (token.IsCancellationRequested) return Selection(OutfitSelectionStatus.Cancelled, outfitId, null, request);
        if (outfitId == CurrentOutfitId)
            return Selection(OutfitSelectionStatus.AlreadySelected, outfitId, _catalog.Warning, request);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        try
        {
            var validated = await _catalog.GetValidatedAsync(outfitId).WaitAsync(linked.Token);
            if (!IsLatest(request)) return Selection(OutfitSelectionStatus.Superseded, outfitId, null, request);
            if (!validated.Availability.IsAvailable)
                return Selection(OutfitSelectionStatus.Unavailable, outfitId, validated.Availability.Warning, request);
            var assets = validated.Assets!;
            // Keep the old suite intact until a complete neutral + common-action candidate is decoded.
            var candidate = await Task.Run(() =>
            {
                linked.Token.ThrowIfCancellationRequested();
                var neutral = LoadFrame(_catalog.Resources, assets.Neutral, validated.Layers);
                var frames = CommonClips.ToDictionary(kind => kind,
                    kind => LoadSequence(_catalog.Resources, assets.Actions.Single(x => x.Clip == kind.ToString()), linked.Token, validated.Layers));
                return (Neutral: neutral, Frames: frames);
            }, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!IsLatest(request)) return Selection(OutfitSelectionStatus.Superseded, outfitId, null, request);
            // A scene may have started during validation/decoding. Never commit over its active frames.
            if (!IsAtSafeOutfitBoundary) return Selection(OutfitSelectionStatus.UnsafeBoundary, outfitId, "动作尚未结束，换装已延期。", request);
            _outfitGeneration++;
            _assets = assets;
            _layers = validated.Layers;
            _neutral = candidate.Neutral;
            _frames.Clear();
            _loads.Clear();
            _clipLoadGenerations.Clear();
            foreach (var pair in candidate.Frames) _frames.Add(pair.Key, pair.Value);
            CurrentOutfitId = outfitId;
            _displayedKind = ClipKind.Idle;
            _target.Source = _displayed = _neutral;
            return Selection(OutfitSelectionStatus.Applied, outfitId, null, request);
        }
        catch (OperationCanceledException)
        {
            return Selection(IsLatest(request) ? OutfitSelectionStatus.Cancelled : OutfitSelectionStatus.Superseded, outfitId, null, request);
        }
        catch (Exception ex) when (OutfitCatalog.IsResourceError(ex))
        {
            return Selection(IsLatest(request) ? OutfitSelectionStatus.Unavailable : OutfitSelectionStatus.Superseded,
                outfitId, "服装切换失败，保留原外观：" + ex.Message, request);
        }
    }

    /// <summary>Startup-only restoration. Missing saved resources leave the initially selected default visible.</summary>
    public async Task<OutfitSelectionResult> RestoreOutfitAsync(string outfitId, CancellationToken token = default)
    {
        var selection = SelectOutfitAsync(outfitId, token);
        long request = _selectionRequest;
        var result = await selection;
        if (IsLatest(request) && result.Status == OutfitSelectionStatus.Unavailable && CurrentOutfitId == OutfitCatalog.DefaultId)
        {
            Warning = "保存的服装暂不可用，已显示默认外观；所有权与装备记录不变。 " + result.Warning;
            return result with { UsedFallback = true, Warning = Warning };
        }
        return result;
    }

    private bool IsLatest(long request) => !_disposed && request == _selectionRequest;

    private OutfitSelectionResult Selection(OutfitSelectionStatus status, string requestedId, string? warning, long request)
    {
        if (IsLatest(request) && status != OutfitSelectionStatus.Cancelled) Warning = warning;
        return new(status, requestedId, CurrentOutfitId, warning);
    }

    public void Apply(ClipSample sample)
    {
        VerifyActive();
        _currentKind = sample.Kind;
        BitmapSource source;
        if (sample.Kind == ClipKind.Idle) source = _neutral;
        else if (_frames.TryGetValue(sample.Kind, out var frames))
        {
            int index = Math.Clamp((int)Math.Round(sample.Progress * (frames.Length - 1)), 0, frames.Length - 1);
            source = frames[index];
        }
        else
        {
            Warning = $"动作 {sample.Kind} 尚未预载，保留当前服装画面。";
            return; // Never silently undress or substitute another pose halfway through an action.
        }
        _displayedKind = sample.Kind;
        if (ReferenceEquals(source, _displayed)) return;
        _target.Source = _displayed = source;
    }

    /// <summary>Independent shop preview; neither the live player's selection nor its cache is touched.</summary>
    public static async Task<IReadOnlyList<BitmapSource>> LoadOutfitPreviewFramesAsync(OutfitCatalog catalog,
        string outfitId, ClipKind clip = ClipKind.Idle, CancellationToken token = default)
    {
        var validated = await catalog.GetValidatedAsync(outfitId).WaitAsync(token).ConfigureAwait(false);
        if (!validated.Availability.IsAvailable) throw new InvalidDataException(validated.Availability.Warning);
        var assets = validated.Assets!;
        return await Task.Run<IReadOnlyList<BitmapSource>>(() =>
        {
            token.ThrowIfCancellationRequested();
            if (clip == ClipKind.Idle) return Array.AsReadOnly(new[] { LoadFrame(catalog.Resources, assets.Neutral, validated.Layers) });
            var action = assets.Actions.SingleOrDefault(x => x.Clip == clip.ToString()) ??
                         throw new InvalidDataException("This outfit does not support the preview action: " + clip);
            return Array.AsReadOnly(LoadSequence(catalog.Resources, action, token, validated.Layers));
        }, token).ConfigureAwait(false);
    }

    private static BitmapSource[] LoadSequence(IAnimationResourceProvider resources, ActionAsset action, CancellationToken token, LayeredOutfitPack? layers = null)
    {
        var result = new BitmapSource[action.FrameCount];
        for (int i = 0; i < result.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            result[i] = LoadFrame(resources, $"{action.Directory}/frame-{i:0000}.png", layers);
        }
        return result;
    }

    internal static BitmapSource LoadFrame(IAnimationResourceProvider resources, string path, LayeredOutfitPack? layers = null) =>
        layers is null ? OutfitBitmap.Load(resources, path) : layers.Load(path);

    // Generic scene-prop loader retained for existing work renderer callers (props need not be 384x346).
    internal static BitmapSource LoadBitmap(string path)
    {
        using var stream = AnimationResourcePath.ReadBounded(PackAnimationResourceProvider.Instance, path, 16 * 1024 * 1024);
        return OutfitBitmap.Decode(stream);
    }

    private void VerifyActive()
    {
        _target.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        _target.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _selectionRequest++;
        _outfitGeneration++;
        _lifetime.Cancel();
        _frames.Clear();
        _loads.Clear();
        _clipLoadGenerations.Clear();
        // In-flight loads still use the gate and cancellation source; do not dispose them underneath continuations.
    }
}
