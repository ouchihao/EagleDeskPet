using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuckDeskPet.Core;

namespace DuckDeskPet;

internal sealed class LoafingSpriteLayer(LoafingAssets assets) : FrameworkElement
{
    private LoafingVisualTimeline? _timeline;
    private BitmapSource? _body, _deskBack, _deskFront, _computer;
    private bool _fallback;
    internal void Present(LoafingVisualTimeline timeline, bool fallback, BitmapSource? body,
        BitmapSource? deskBack, BitmapSource? deskFront, BitmapSource? computer)
    {
        _timeline = timeline; _fallback = fallback; _body = body;
        _deskBack = deskBack; _deskFront = deskFront; _computer = computer;
        Visibility = timeline.Active ? Visibility.Visible : Visibility.Collapsed;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double scale = Math.Min(ActualWidth / 384, ActualHeight / 346);
        dc.PushTransform(new TranslateTransform((ActualWidth - 384 * scale) / 2, (ActualHeight - 346 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        DrawScene(dc);
        dc.Pop(); dc.Pop();
    }

    internal void DrawScene(DrawingContext dc)
    {
        if (_timeline is not { Active: true } t) return;
        if (_fallback) DrawFallbackBody(dc, t);
        if (t.SpriteVisible)
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 302, 384, 44)));
            dc.DrawImage(assets.Feet, new Rect(0, 0, 384, 346)); dc.Pop();
            double x = t.Kind == LoafingKind.WoodenFish ? 75 : 77;
            double y = (t.Kind == LoafingKind.WoodenFish ? 20 : 42) + t.SpriteOffsetY;
            var frame = assets.Frame(t.Kind, t.LoopSeconds);
            // The source GIFs are waist-up: body stays behind the actual equipped desk
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, 384, 276)));
            dc.DrawImage(frame, new Rect(x, y, 250, 250)); dc.Pop();
            dc.DrawImage(_deskBack, new Rect(0, 0, 384, 346));
            dc.DrawImage(_deskFront, new Rect(0, 0, 384, 346));
            if (t.Kind == LoafingKind.WoodenFish) DrawWoodenFish(dc, t, y);
        }
        else if (_fallback)
        {
            dc.DrawImage(_deskBack, new Rect(0, 0, 384, 346));
            dc.DrawImage(_deskFront, new Rect(0, 0, 384, 346));
        }
        if (_fallback) DrawFallbackComputer(dc, t);
    }

    private void DrawWoodenFish(DrawingContext dc, LoafingVisualTimeline t, double actorY)
    {
        double offset = t.SpriteOffsetY;
        // A planted grip plus an eased downstroke, not a free-floating prop
        double beat = t.LoopSeconds % 1.36 / 1.36;
        double strike = beat < .68 ? LoafingVisualTimeline.Ease(beat / .68) :
            1 - LoafingVisualTimeline.Ease((beat - .68) / .32);
        double impact = Math.Max(0, 1 - Math.Abs(beat - .68) / .08);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, 384, 286)));
        dc.DrawImage(assets.Fish, new Rect(217, 255 + offset + 2 * impact, 46, 29 - 2 * impact));
        dc.PushTransform(new RotateTransform(18 + 98 * strike, 202, actorY + 213));
        dc.DrawImage(assets.Mallet, new Rect(197, actorY + 153, 18, 63));
        dc.Pop(); dc.Pop();
    }

    private void DrawFallbackBody(DrawingContext dc, LoafingVisualTimeline t)
    {
        double dy = t.Phase switch
        {
            LoafingPhase.Throw => 300 * LoafingVisualTimeline.Ease((t.Seconds - 1.15) / .8),
            LoafingPhase.Return => 300 * (1 - LoafingVisualTimeline.Ease((t.Seconds - .8) / .8)),
            _ => 300
        };
        if (dy >= 300) return;
        if (dy > 0) dc.PushClip(new RectangleGeometry(new Rect(0, 0, 384, 276)));
        dc.DrawImage(_body, new Rect(0, dy, 384, 346));
        if (dy > 0) dc.Pop();
    }

    private void DrawFallbackComputer(DrawingContext dc, LoafingVisualTimeline t)
    {
        double u;
        if (t.Phase == LoafingPhase.Throw) u = Math.Clamp((t.Seconds - .25) / .9, 0, 1);
        else if (t.Phase == LoafingPhase.Return) u = 1 - LoafingVisualTimeline.Ease((t.Seconds - 1.4) / 1.4);
        else return;
        if (u >= 1) return;
        double x = t.Phase == LoafingPhase.Throw ? 440 * u * u : 440 * u;
        double y = t.Phase == LoafingPhase.Throw ? -340 * u + 40 * u * u : -300 * u;
        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new RotateTransform(-270 * u, 192, 244));
        dc.DrawImage(_computer, new Rect(0, 0, 384, 346));
        dc.Pop(); dc.Pop();
    }
}
