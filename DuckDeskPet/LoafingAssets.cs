using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuckDeskPet.Core;

namespace DuckDeskPet;

/// <summary>Original GIF frames stay untouched; only the runtime matte removes sticker backgrounds</summary>
internal sealed class LoafingAssets
{
    internal BitmapSource[] Nose { get; }
    internal BitmapSource[] Monk { get; }
    internal BitmapSource Fish { get; }
    internal BitmapSource Mallet { get; }
    internal BitmapSource Feet { get; }

    internal LoafingAssets(Func<string, BitmapSource>? load = null)
    {
        load ??= LoadNative;
        var monkMask = Matte(Pixels(load("Assets/Loafing/buddha/frame-0000.png")), false);
        Nose = Enumerable.Range(0, 35).Select(i => Cut(load($"Assets/Loafing/nose/frame-{i:0000}.png"), true, null)).ToArray();
        Monk = Enumerable.Range(0, 35).Select(i => Cut(load($"Assets/Loafing/buddha/frame-{i:0000}.png"), false, monkMask)).ToArray();
        var atlas = load("Assets/Loafing/props-atlas.png");
        Fish = CropHalf(atlas, false); Mallet = CropHalf(atlas, true);
        Feet = load("Assets/mascot-animated-neutral.png");
    }

    internal BitmapSource Frame(LoafingKind kind, double seconds) =>
        (kind == LoafingKind.WoodenFish ? Monk : Nose)[LoafingVisualTimeline.SourceFrame(seconds)];

    private static BitmapSource LoadNative(string path)
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/" + path))!.Stream;
        var bitmap = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        bitmap.Freeze(); return bitmap;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var readable = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        readable.CopyPixels(pixels, source.PixelWidth * 4, 0); return pixels;
    }

    private static byte[] Matte(byte[] pixels, bool nose)
    {
        const int size = 250;
        var alpha = new byte[size * size];
        for (int y = 48; y < size; y++) for (int x = 0; x < size; x++)
        {
            int i = y * size + x, p = i * 4;
            double b = pixels[p], g = pixels[p + 1], r = pixels[p + 2];
            // The supplied GIFs use #445BB5; the eagle contains no blue material
            double a = Math.Clamp((90 - (b - Math.Max(r, g))) / 90, 0, 1);
            if (a < .08) a = 0;
            if (nose && y >= 218 && r > 230 && g > 220 && b > 115) a = 0;
            alpha[i] = (byte)Math.Round(a * pixels[p + 3]);
        }
        // Keep only the actor connected to the face, excluding captions and isolated ellipsis marks
        var keep = new bool[alpha.Length]; var queue = new Queue<int>();
        queue.Enqueue(125 * size + 115); keep[125 * size + 115] = true;
        while (queue.TryDequeue(out int i))
        {
            int x = i % size, y = i / size;
            if (x > 0) Visit(i - 1); if (x < 249) Visit(i + 1);
            if (y > 0) Visit(i - size); if (y < 249) Visit(i + size);
        }
        for (int i = 0; i < alpha.Length; i++) if (!keep[i]) alpha[i] = 0;
        var edgeAlpha = (byte[])alpha.Clone();
        for (int y = 2; y < size - 2; y++) for (int x = 2; x < size - 2; x++)
        {
            int i = y * size + x;
            if (alpha[i] == 0) continue;
            bool boundary = false;
            for (int yy = y - 2; yy <= y + 2 && !boundary; yy++)
            for (int xx = x - 2; xx <= x + 2; xx++)
                if (edgeAlpha[yy * size + xx] == 0) { boundary = true; break; }
            if (!boundary) alpha[i] = 255;
        }
        return alpha;
        void Visit(int i) { if (!keep[i] && alpha[i] > 0) { keep[i] = true; queue.Enqueue(i); } }
    }

    private static BitmapSource Cut(BitmapSource source, bool nose, byte[]? stableMask)
    {
        if (source.PixelWidth != 250 || source.PixelHeight != 250)
            throw new System.IO.InvalidDataException("Loafing source must retain its native 250 × 250 pixels");
        var pixels = Pixels(source); var alpha = stableMask ?? Matte(pixels, nose);
        for (int i = 0; i < alpha.Length; i++)
        {
            int p = i * 4; double a = alpha[i] / 255d;
            if (a is > 0 and < 1)
            {
                // Undo the blue matte at antialiased boundaries instead of leaving a blue/white fringe
                pixels[p] = (byte)Math.Clamp((pixels[p] - 181 * (1 - a)) / a, 0, 255);
                pixels[p + 1] = (byte)Math.Clamp((pixels[p + 1] - 91 * (1 - a)) / a, 0, 255);
                pixels[p + 2] = (byte)Math.Clamp((pixels[p + 2] - 68 * (1 - a)) / a, 0, 255);
            }
            pixels[p + 3] = alpha[i];
        }
        var bitmap = BitmapSource.Create(250, 250, 96, 96, PixelFormats.Bgra32, null, pixels, 1000);
        bitmap.Freeze(); return bitmap;
    }

    private static BitmapSource CropHalf(BitmapSource atlas, bool right)
    {
        var pixels = Pixels(atlas); int w = atlas.PixelWidth, h = atlas.PixelHeight;
        int minX = w, minY = h, maxX = 0, maxY = 0;
        int divider = (int)(w * .62);
        for (int y = 0; y < h; y++) for (int x = right ? divider : 0; x < (right ? w : divider); x++)
        {
            if (pixels[(y * w + x) * 4 + 3] < 24) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        var crop = new CroppedBitmap(atlas, new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
        crop.Freeze(); return crop;
    }
}
