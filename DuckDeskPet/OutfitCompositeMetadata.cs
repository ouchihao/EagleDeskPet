using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace DuckDeskPet;

/// <summary>Semantic occlusion always uses the undressed pose, not arbitrary garment colours.</summary>
internal static class OutfitCompositeMetadata
{
    private static readonly ConditionalWeakTable<BitmapSource, BitmapSource> Sources = new();
    internal static void Attach(BitmapSource dressed, BitmapSource anatomicalSource)
    {
        // Retain a tiny three-colour semantic source, not a second full RGBA animation suite.
        // These are exactly the two predicates consumed by SceneForegroundMask.Create.
        var readable = new FormatConvertedBitmap(anatomicalSource, PixelFormats.Bgra32, null, 0);
        int width = readable.PixelWidth, height = readable.PixelHeight;
        var pixels = new byte[width * height * 4]; readable.CopyPixels(pixels, width * 4, 0);
        var labels = new byte[width * height];
        for (int i = 0; i < labels.Length; i++)
        {
            int p = i * 4, b = pixels[p], g = pixels[p + 1], r = pixels[p + 2];
            if (pixels[p + 3] != 0) labels[i] = 3; // Original alpha coverage excludes capes/backpacks from desk foreground.
            if (pixels[p + 3] < 180) continue;
            if (r - g >= 25 && g - b >= 12 && r <= 177 && g <= 103) labels[i] = 1;
            else if (r >= 145 && g >= 132 && b >= 98 && r - g <= 50 && g - b <= 80) labels[i] = 2;
        }
        var palette = new BitmapPalette(new[] { Colors.Transparent, Color.FromRgb(150, 75, 35), Color.FromRgb(230, 220, 190), Colors.Black });
        var semantic = BitmapSource.Create(width, height, 96, 96, PixelFormats.Indexed8, palette, labels, width);
        semantic.Freeze(); Sources.Add(dressed, semantic);
    }
    internal static BitmapSource Anatomy(BitmapSource bitmap) => Sources.TryGetValue(bitmap, out var source) ? source : bitmap;
}
