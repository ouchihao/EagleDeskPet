using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuckDeskPet.Core;

namespace DuckDeskPet;

internal sealed class WorkStageRenderer
{
    private const double CanvasWidth = 384;
    private const double CanvasHeight = 346;
    private readonly Image _back;
    private readonly Image _front;
    private readonly Image _laptop;
    private readonly Image _fire;
    // WPF Image already places its Uniform-stretched content inside the 174 DIP
    // slot. RenderTransform coordinates use that image's local drawing bounds;
    // adding the letterbox again makes a shrinking flame sink below the desk.
    private readonly ScaleTransform _fireGrowth = new(1, 0, 80, 334 * (160.0 / 384));
    private BitmapSource[]? _fireFrames;
    private double _fireSeconds;
    private ClipSample _previous;
    private readonly TranslateTransform _deskBackMotion = new();
    private readonly TranslateTransform _deskFrontMotion = new();
    private readonly TranslateTransform _laptopMotion = new();

    internal WorkStageRenderer(Image back, Image front, Image laptop, Image fire)
    {
        _back = back;
        _front = front;
        _laptop = laptop;
        _fire = fire;
        // V2's authored modesty panel occludes the seated lower body and feet.
        // Only the near tabletop/desk structure overlays the eagle; typing wings
        // stay above the rear surface instead of bisecting its front edge.
        _back.Source = RasterFramePlayer.LoadBitmap("Assets/SceneProps/WorkV2/desk-back.png");
        _front.Source = RasterFramePlayer.LoadBitmap("Assets/SceneProps/WorkV2/desk-front.png");
        _laptop.Source = RasterFramePlayer.LoadBitmap("Assets/SceneProps/Work/laptop.png");
        _back.RenderTransform = _deskBackMotion;
        _front.RenderTransform = _deskFrontMotion;
        _laptop.RenderTransform = _laptopMotion;
        _fire.RenderTransform = _fireGrowth;
        Apply(ClipSample.Idle);
    }

    internal async Task WarmFireAsync()
    {
        if (_fireFrames is not null) return;
        _fireFrames = await Task.Run(() => Enumerable.Range(0, 121)
            .Select(i => RasterFramePlayer.LoadBitmap($"Assets/SceneProps/WorkV2/Fire/frame-{i:0000}.png")).ToArray());
    }

    internal void ReleaseFireFrames()
    {
        _fireFrames = null;
        _fire.Source = null;
        _fireSeconds = 0;
    }

    internal void Apply(ClipSample clip, double deltaSeconds = 0)
    {
        var motion = WorkStageMotion.Sample(clip);
        var visibility = motion.Visible ? Visibility.Visible : Visibility.Collapsed;
        _back.Visibility = _front.Visibility = _laptop.Visibility = visibility;
        // Uniform image letterboxing matches PetImage exactly; parent scaling/DPI applies to all layers once.
        double scale = Math.Min(_back.Width / CanvasWidth, _back.Height / CanvasHeight);
        _deskBackMotion.X = _deskFrontMotion.X = motion.DeskXPixels * scale;
        _laptopMotion.Y = motion.LaptopYPixels * scale;
        bool firePhase = clip.Kind is ClipKind.WorkToBusy or ClipKind.BusyLoop or ClipKind.BusyExit;
        if (!firePhase) _fireSeconds = 0;
        else if (clip != _previous) _fireSeconds += Math.Clamp(deltaSeconds, 0, ClipTimeline.MaximumDeltaSeconds);
        _previous = clip;
        double growth = WorkStageMotion.FireGrowth(clip);
        _fireGrowth.ScaleY = growth;
        _fire.Visibility = growth > 0 && _fireFrames is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_fireFrames is not null && firePhase)
            _fire.Source = _fireFrames[(int)Math.Floor(_fireSeconds * 60 + 1e-8) % 120];
    }
}
