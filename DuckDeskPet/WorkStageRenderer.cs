using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.CompilerServices;
using DuckDeskPet.Core;

namespace DuckDeskPet;

internal sealed class WorkStageRenderer
{
    private readonly Image _back;
    private readonly Image _front;
    private readonly Image _laptop;
    private readonly Image _fire;
    private readonly Image? _character;
    private readonly Image? _foreground;
    private readonly ConditionalWeakTable<BitmapSource, ForegroundMasks> _foregroundMasks = new();
    private BitmapSource? _foregroundSource;
    private bool _foregroundHasBowl;
    // WPF Image already places its Uniform-stretched content inside the 174 DIP
    // slot. RenderTransform coordinates use that image's local drawing bounds;
    // adding the letterbox again makes a shrinking flame sink below the desk.
    private readonly ScaleTransform _fireGrowth = new(1, 0);
    private readonly RectangleGeometry _fireOcclusion = new();
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
    internal Image? ForegroundLayer => _foreground;

    internal WorkStageRenderer(Image back, Image front, Image laptop, Image fire,
        SceneCatalog? catalog = null, Func<string, BitmapSource>? loadBitmap = null, Image? character = null)
    {
        _back = back;
        _front = front;
        _laptop = laptop;
        _fire = fire;
        _character = character;
        Catalog = catalog ?? LoadCatalog();
        _loadBitmap = loadBitmap ?? RasterFramePlayer.LoadBitmap;
        _active = Prepare(Catalog.Resolve(Catalog.DefaultSelection));
        ShowPreparedProps();
        _back.RenderTransform = _deskBackMotion;
        _front.RenderTransform = _deskFrontMotion;
        _laptop.RenderTransform = _laptopMotion;
        _fire.RenderTransform = _fireGrowth;
        _fire.Clip = _fireOcclusion;
        if (character is not null)
        {
            if (character.Parent is not Panel panel || !ReferenceEquals(back.Parent, panel))
                throw new ArgumentException("The scene actor and props must share their layer panel.", nameof(character));
            // Fixed semantic depth for the entire scene: body < complete desk < hands/bowl < apron < PC.
            // The tabletop never swaps depth on its arrival/departure frame.
            Panel.SetZIndex(_fire, -1);
            Panel.SetZIndex(character, 0);
            Panel.SetZIndex(_back, 1);
            _foreground = new Image { Width = character.Width, Height = character.Height,
                Stretch = character.Stretch, IsHitTestVisible = false, SnapsToDevicePixels = character.SnapsToDevicePixels };
            RenderOptions.SetBitmapScalingMode(_foreground, RenderOptions.GetBitmapScalingMode(character));
            Panel.SetZIndex(_foreground, 2);
            panel.Children.Add(_foreground);
            Panel.SetZIndex(_front, 3);
            Panel.SetZIndex(_laptop, 4);
            SetForegroundDeskMask();
        }
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
        _foregroundMasks.Clear();
        _foregroundSource = null;
        if (_foreground is not null) { _foreground.Source = null; _foreground.Clip = null; }
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
        UpdateForeground(clip);
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
        // Fire stays behind the fixed tabletop, including its flat authored base. Clip is evaluated
        // before RenderTransform, so invert the anchored growth to keep the screen-space cut fixed.
        double cut = growth > 0 ? scene.Fire.Anchor.Y + (scene.DeskSurfaceAnchor.Y - scene.Fire.Anchor.Y) / growth : 0;
        _fireOcclusion.Rect = new Rect(0, 0, scene.Canvas.Width * fireScale, Math.Clamp(cut, 0, scene.Canvas.Height) * fireScale);
        _fire.Visibility = growth > 0 && _active.FireFrames is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_active.FireFrames is not null && firePhase)
            _fire.Source = _active.FireFrames[(int)Math.Floor(_fireSeconds * scene.Fire.SampleRate + 1e-8) % scene.Fire.LoopFrameCount];
    }

    private void UpdateForeground(ClipSample clip)
    {
        if (_foreground is null) return;
        bool visible = ClipCatalog.IsWorkScene(clip.Kind) || ClipCatalog.IsHungryScene(clip.Kind);
        _foreground.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) { _foreground.Source = null; _foregroundSource = null; return; }
        if (_character?.Source is not BitmapSource source) { _foreground.Source = null; return; }
        bool hasBowl = ClipCatalog.IsHungryScene(clip.Kind);
        if (ReferenceEquals(_foregroundSource, source) && _foregroundHasBowl == hasBowl) return;
        _foregroundSource = source;
        _foregroundHasBowl = hasBowl;
        _foreground.Source = source;
        _foreground.RenderTransform = _character.RenderTransform;
        var masks = _foregroundMasks.GetValue(source, _ => new());
        double scale = Math.Min(_back.Width / CurrentScene.Canvas.Width, _back.Height / CurrentScene.Canvas.Height);
        _foreground.Clip = hasBowl
            ? masks.Hungry ??= SceneForegroundMask.Create(source, CurrentScene.Canvas, scale, includeBowl: true)
            : masks.Work ??= SceneForegroundMask.Create(source, CurrentScene.Canvas, scale, includeBowl: false);
    }

    private void ShowPreparedProps()
    {
        // Back/front are an indivisible pair; a pending selection never mixes the old and new desk.
        _back.Source = _active.Back;
        _front.Source = _active.Front;
        _laptop.Source = _active.Computer;
        SetForegroundDeskMask();
    }

    private void SetForegroundDeskMask()
    {
        if (_foreground is null) return;
        double scale = Math.Min(_back.Width / CurrentScene.Canvas.Width, _back.Height / CurrentScene.Canvas.Height);
        // Preserve the original actor's edge alpha while the table is offscreen: the foreground
        // copy only paints where the real, translated desk has coverage (not over bare background).
        _foreground.OpacityMask = new ImageBrush(_active.DeskCoverage)
        {
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, CurrentScene.Canvas.Width * scale, CurrentScene.Canvas.Height * scale),
            Stretch = Stretch.Fill, TileMode = TileMode.None, Transform = _deskBackMotion,
        };
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
        internal BitmapSource DeskCoverage { get; } = SceneForegroundMask.CreateDeskCoverage(back);
        internal BitmapSource Front { get; } = front;
        internal BitmapSource Computer { get; } = computer;
        internal BitmapSource[]? FireFrames { get; set; }
    }
    private sealed class ForegroundMasks
    {
        internal Geometry? Work;
        internal Geometry? Hungry;
    }
}

/// <summary>
/// The v1 eagle-work slot has brown outlined hands and an optional cream bowl.
/// Find their actual inner contours only in the tabletop band, rather than using
/// rectangular holes that expose the torso. The source art itself is never edited.
/// This small mask is cached by immutable frame identity and weakly held.
/// </summary>
internal static class SceneForegroundMask
{
    internal static BitmapSource CreateDeskCoverage(BitmapSource desk)
    {
        // The top surface's antialiased edge must not partially suppress an opaque foreground bowl.
        // Prepare a binary, two-texel safety envelope once with the desk, never in Apply/Rendering.
        var readable = new FormatConvertedBitmap(desk, PixelFormats.Pbgra32, null, 0);
        int width = desk.PixelWidth, height = desk.PixelHeight;
        var pixels = new byte[width * height * 4]; readable.CopyPixels(pixels, width * 4, 0);
        var coverage = new byte[pixels.Length];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            if (pixels[(y * width + x) * 4 + 3] == 0) continue;
            for (int yy = Math.Max(0, y - 2); yy <= Math.Min(height - 1, y + 2); yy++)
            for (int xx = Math.Max(0, x - 2); xx <= Math.Min(width - 1, x + 2); xx++)
            {
                int p = (yy * width + xx) * 4;
                coverage[p] = coverage[p + 1] = coverage[p + 2] = coverage[p + 3] = 255;
            }
        }
        var mask = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, coverage, width * 4);
        mask.Freeze(); return mask;
    }

    internal static Geometry Create(BitmapSource source, SceneCanvas canvas, double sceneScale, bool includeBowl)
    {
        double sx = source.PixelWidth / canvas.Width, sy = source.PixelHeight / canvas.Height;
        const double bandTop = 247, bandBottom = 278;
        int left = (int)Math.Floor(104 * sx), top = (int)Math.Floor(bandTop * sy);
        int right = (int)Math.Ceiling(280 * sx), bottom = (int)Math.Ceiling(bandBottom * sy);
        int width = right - left, height = bottom - top;
        var readable = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32 ? source :
            new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[width * height * 4];
        readable.CopyPixels(new Int32Rect(left, top, width, height), pixels, width * 4, 0);
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var drawing = geometry.Open())
        {
            for (int y = 0; y < height; y++)
            {
                double sceneY = (top + y) / sy;
                double rowHeight = 1 / sy;
                int leftEdge = FindOutline(y, 139, 182, fromRight: true);
                int rightEdge = FindOutline(y, 204, 247, fromRight: false);
                // Missing brown outline (e.g. sleeve art) fails closed to the outside of the body,
                // never opens a central hole. This contract is exercised against every actual scene frame.
                double leftHandEnd = leftEdge < 0 ? 137 : (leftEdge + left + 1.2) / sx;
                double rightHandStart = rightEdge < 0 ? 249 : (rightEdge + left - .2) / sx;
                Rectangle(0, sceneY, leftHandEnd, rowHeight);
                Rectangle(rightHandStart, sceneY, canvas.Width - rightHandStart, rowHeight);

                // A cream shirt/badge is not a bowl. Only the explicitly hungry action family
                // authorizes the held-bowl foreground; work frames always keep the torso behind.
                if (!includeBowl) continue;
                int bowlLeft = -1, bowlRight = -1, lightPixels = 0;
                for (int yy = Math.Max(0, y - 2); yy <= Math.Min(height - 1, y + 2); yy++)
                for (int x = Math.Max(0, (int)(151 * sx) - left); x <= Math.Min(width - 1, (int)(233 * sx) - left); x++)
                {
                    int p = (yy * width + x) * 4; int b = pixels[p], g = pixels[p + 1], r = pixels[p + 2];
                    if (pixels[p + 3] < 180 || r < 145 || g < 132 || b < 98 || r - g > 50 || g - b > 80) continue;
                    bowlLeft = bowlLeft < 0 ? x : Math.Min(bowlLeft, x); bowlRight = Math.Max(bowlRight, x); lightPixels++;
                }
                if (lightPixels >= 8 && bowlRight - bowlLeft >= 12 * sx)
                    Rectangle((left + bowlLeft - 2.2) / sx, sceneY, (bowlRight - bowlLeft + 5.4) / sx, rowHeight);
            }

            void Rectangle(double x, double y, double w, double h)
            {
                // Native row strips give WPF a true alpha-bearing copy of the current hand/bowl,
                // including its ink marks; no per-frame bitmap allocation, re-encoding or recoloring.
                drawing.BeginFigure(new Point(x * sceneScale, y * sceneScale), true, true);
                drawing.LineTo(new Point((x + w) * sceneScale, y * sceneScale), true, false);
                drawing.LineTo(new Point((x + w) * sceneScale, (y + h) * sceneScale), true, false);
                drawing.LineTo(new Point(x * sceneScale, (y + h) * sceneScale), true, false);
            }
        }
        geometry.Freeze(); return geometry;

        int FindOutline(int y, double start, double end, bool fromRight)
        {
            int first = Math.Max(0, (int)Math.Floor(start * sx) - left);
            int last = Math.Min(width - 1, (int)Math.Ceiling(end * sx) - left);
            for (int i = 0; i <= last - first; i++)
            {
                int x = fromRight ? last - i : first + i, p = (y * width + x) * 4;
                int b = pixels[p], g = pixels[p + 1], r = pixels[p + 2];
                if (pixels[p + 3] >= 180 && r - g >= 25 && g - b >= 12 && r <= 177 && g <= 103) return x;
            }
            return -1;
        }
    }
}
