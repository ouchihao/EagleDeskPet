using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuckDeskPet.Core;

namespace DuckDeskPet;

internal sealed class WorkStageRenderer
{
    private readonly Image _back;
    private readonly Image _front;
    private readonly Image _laptop;
    private readonly Image _fire;
    // WPF Image already places its Uniform-stretched content inside the 174 DIP
    // slot. RenderTransform coordinates use that image's local drawing bounds;
    // adding the letterbox again makes a shrinking flame sink below the desk.
    private readonly ScaleTransform _fireGrowth = new(1, 0);
    private readonly Func<string, BitmapSource> _loadBitmap;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private PreparedScene _active;
    private PreparedScene? _pending;
    private bool _fireWanted;
    private long _selectionRevision;
    private double _fireSeconds;
    private ClipSample _previous;
    private readonly TranslateTransform _deskBackMotion = new();
    private readonly TranslateTransform _deskFrontMotion = new();
    private readonly TranslateTransform _laptopMotion = new();

    internal SceneCatalog Catalog { get; }
    internal SceneSelection ActiveSelection => _active.Definition.Selection;
    internal SceneSelection? PendingSelection => _pending?.Definition.Selection;
    internal SceneDefinition CurrentScene => _active.Definition.Scene;

    internal WorkStageRenderer(Image back, Image front, Image laptop, Image fire,
        SceneCatalog? catalog = null, Func<string, BitmapSource>? loadBitmap = null)
    {
        _back = back;
        _front = front;
        _laptop = laptop;
        _fire = fire;
        Catalog = catalog ?? LoadCatalog();
        _loadBitmap = loadBitmap ?? RasterFramePlayer.LoadBitmap;
        _active = Prepare(Catalog.Resolve(Catalog.DefaultSelection));
        ShowPreparedProps();
        _back.RenderTransform = _deskBackMotion;
        _front.RenderTransform = _deskFrontMotion;
        _laptop.RenderTransform = _laptopMotion;
        _fire.RenderTransform = _fireGrowth;
        Apply(ClipSample.Idle);
    }

    internal async Task WarmFireAsync()
    {
        _fireWanted = true;
        await _loadGate.WaitAsync();
        try
        {
            var target = _pending ?? _active;
            if (target.FireFrames is null)
            {
                var frames = await LoadFireAsync(target.Definition.Scene.Fire);
                if (_fireWanted) target.FireFrames = frames;
            }
        }
        finally { _loadGate.Release(); }
    }

    /// <summary>Prepare an entire compatible set off the render path. Latest request wins.
    /// A live work scene, including its exit, keeps its old set until the next safe boundary.</summary>
    internal async Task RequestSelectionAsync(SceneSelection selection)
    {
        var definition = Catalog.Resolve(selection);
        long revision = ++_selectionRevision;
        await _loadGate.WaitAsync();
        try
        {
            if (revision != _selectionRevision) return;
            if (selection == ActiveSelection) { _pending = null; return; }
            var prepared = await Task.Run(() => Prepare(definition));
            if (_fireWanted)
            {
                var frames = _active.Definition.Scene.Fire == definition.Scene.Fire && _active.FireFrames is not null
                    ? _active.FireFrames : await LoadFireAsync(definition.Scene.Fire);
                if (_fireWanted) prepared.FireFrames = frames;
            }
            if (revision == _selectionRevision) _pending = prepared;
        }
        finally { _loadGate.Release(); }
    }

    internal void ReleaseFireFrames()
    {
        _fireWanted = false;
        _active.FireFrames = null;
        if (_pending is not null) _pending.FireFrames = null;
        _fire.Source = null;
        _fireSeconds = 0;
    }

    internal void Apply(ClipSample clip, double deltaSeconds = 0)
    {
        if (_pending is not null && (clip.Kind == ClipKind.Idle ||
            (clip.Kind == ClipKind.WorkEnter && clip.Progress == 0 && !ClipCatalog.IsWorkScene(_previous.Kind))))
        {
            _active = _pending;
            _pending = null;
            _fire.Source = null;
            _fireSeconds = 0;
            ShowPreparedProps();
        }
        var scene = CurrentScene;
        if (ClipCatalog.IsHungryScene(clip.Kind))
        {
            // Same replaceable table and occlusion contract, but no computer or flames.
            // The actor stays planted while the table slides into its own layer.
            double t = clip.Kind == ClipKind.HungryEnter ? Math.Clamp(clip.ElapsedSeconds / 0.65, 0, 1) :
                clip.Kind == ClipKind.HungryExit ? 1 - Math.Clamp((clip.ElapsedSeconds - 0.75) / 0.75, 0, 1) : 1;
            double ease = t * t * (3 - 2 * t);
            double pixels = -scene.Motion.DeskTravelPixels * (1 - ease);
            double tableScale = Math.Min(_back.Width / scene.Canvas.Width, _back.Height / scene.Canvas.Height);
            _deskBackMotion.X = _deskFrontMotion.X = pixels * tableScale;
            _back.Visibility = _front.Visibility = Visibility.Visible;
            _laptop.Visibility = _fire.Visibility = Visibility.Collapsed;
            _fireSeconds = 0;
            _previous = clip;
            return;
        }
        var motion = WorkStageMotion.Sample(clip, scene);
        var visibility = motion.Visible ? Visibility.Visible : Visibility.Collapsed;
        _back.Visibility = _front.Visibility = _laptop.Visibility = visibility;
        // Uniform image letterboxing matches PetImage exactly; parent scaling/DPI applies to all layers once.
        double scale = Math.Min(_back.Width / scene.Canvas.Width, _back.Height / scene.Canvas.Height);
        _deskBackMotion.X = _deskFrontMotion.X = motion.DeskXPixels * scale;
        _laptopMotion.Y = motion.LaptopYPixels * scale;
        bool firePhase = clip.Kind is ClipKind.WorkToBusy or ClipKind.BusyLoop or ClipKind.BusyExit;
        if (!firePhase) _fireSeconds = 0;
        else if (clip != _previous) _fireSeconds += Math.Clamp(deltaSeconds, 0, ClipTimeline.MaximumDeltaSeconds);
        _previous = clip;
        double growth = WorkStageMotion.FireGrowth(clip, scene);
        double fireScale = Math.Min(_fire.Width / scene.Canvas.Width, _fire.Height / scene.Canvas.Height);
        _fireGrowth.CenterX = scene.Fire.Anchor.X * fireScale;
        _fireGrowth.CenterY = scene.Fire.Anchor.Y * fireScale;
        _fireGrowth.ScaleY = growth;
        _fire.Visibility = growth > 0 && _active.FireFrames is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_active.FireFrames is not null && firePhase)
            _fire.Source = _active.FireFrames[(int)Math.Floor(_fireSeconds * scene.Fire.SampleRate + 1e-8) % scene.Fire.LoopFrameCount];
    }

    private void ShowPreparedProps()
    {
        // Back/front are an indivisible pair; a pending selection never mixes the old and new desk.
        _back.Source = _active.Back;
        _front.Source = _active.Front;
        _laptop.Source = _active.Computer;
    }

    private PreparedScene Prepare(ResolvedWorkScene definition) => new(definition,
        _loadBitmap(definition.Desk.Back), _loadBitmap(definition.Desk.Front), _loadBitmap(definition.Computer.Image));

    private Task<BitmapSource[]> LoadFireAsync(SceneFireDefinition fire) => Task.Run(() =>
        Enumerable.Range(0, fire.FrameCount).Select(i => _loadBitmap(fire.FramePath(i))).ToArray());

    private static SceneCatalog LoadCatalog()
    {
        using var stream = Application.GetResourceStream(
            new Uri($"pack://application:,,,/{SceneCatalog.ResourcePath}", UriKind.Absolute))?.Stream
            ?? throw new InvalidDataException("Missing work scene catalog.");
        return SceneCatalog.ParseRuntime(stream, path =>
        {
            try
            {
                using var asset = Application.GetResourceStream(new Uri($"pack://application:,,,/{path}", UriKind.Absolute))?.Stream;
                return asset is not null;
            }
            catch (IOException) { return false; }
        });
    }

    private sealed class PreparedScene(ResolvedWorkScene definition, BitmapSource back, BitmapSource front, BitmapSource computer)
    {
        internal ResolvedWorkScene Definition { get; } = definition;
        internal BitmapSource Back { get; } = back;
        internal BitmapSource Front { get; } = front;
        internal BitmapSource Computer { get; } = computer;
        internal BitmapSource[]? FireFrames { get; set; }
    }
}
