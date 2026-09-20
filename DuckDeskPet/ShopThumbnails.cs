using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuckDeskPet.Core;

namespace DuckDeskPet;

/// <summary>Small, frozen shelf portraits from the real installed art, never placeholder emoji or
/// animation players. Loading a shop icon cannot advance the pet, preview or save state.</summary>
internal static class ShopThumbnails
{
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.Ordinal);

    internal static ImageSource? Get(ContentDefinition item)
    {
        if (Cache.TryGetValue(item.Id, out var cached)) return cached;
        string[] paths = item.Id switch
        {
            ContentCatalog.DefaultDeskId => ["Assets/SceneProps/WorkV2/desk-back.png", "Assets/SceneProps/WorkV2/desk-front.png"],
            ContentCatalog.MintDeskId => ["Assets/SceneProps/Shop/desk-mint-back.png", "Assets/SceneProps/Shop/desk-mint-front.png"],
            ContentCatalog.WalnutDeskId => ["Assets/SceneProps/Shop/desk-walnut-back.png", "Assets/SceneProps/Shop/desk-walnut-front.png"],
            ContentCatalog.ArcadeDeskId => ["Assets/SceneProps/Shop/desk-arcade-back.png", "Assets/SceneProps/Shop/desk-arcade-front.png"],
            ContentCatalog.NoodleDeskId => ["Assets/SceneProps/Shop/desk-noodle-back.png", "Assets/SceneProps/Shop/desk-noodle-front.png"],
            ContentCatalog.CloudDeskId => ["Assets/SceneProps/Shop/desk-cloud-back.png", "Assets/SceneProps/Shop/desk-cloud-front.png"],
            ContentCatalog.BoardroomDeskId => ["Assets/SceneProps/Shop/desk-boardroom-back.png", "Assets/SceneProps/Shop/desk-boardroom-front.png"],
            ContentCatalog.DefaultComputerId => ["Assets/SceneProps/Work/laptop.png"],
            ContentCatalog.MidnightComputerId => ["Assets/SceneProps/Shop/computer-midnight.png"],
            ContentCatalog.RetroComputerId => ["Assets/SceneProps/Shop/computer-retro.png"],
            ContentCatalog.ArcadeComputerId => ["Assets/SceneProps/Shop/computer-arcade.png"],
            ContentCatalog.ServerComputerId => ["Assets/SceneProps/Shop/computer-server.png"],
            ContentCatalog.GoldComputerId => ["Assets/SceneProps/Shop/computer-gold.png"],
            ContentCatalog.UltrabookComputerId => ["Assets/SceneProps/Shop/computer-ultrabook.png"],
            ContentCatalog.DefaultOutfitId => ["Assets/mascot-animated-neutral.png"],
            ContentCatalog.OfficeOutfitId => ["Assets/Outfits/Office/thumbnail.png"],
            ContentCatalog.OxOutfitId => ["Assets/Outfits/Ox/thumbnail.png"],
            ContentCatalog.HeroOutfitId => ["Assets/Outfits/Hero/thumbnail.png"],
            ContentCatalog.AstronautOutfitId => ["Assets/Outfits/Astronaut/thumbnail.png"],
            ContentCatalog.HoodieOutfitId => ["Assets/Outfits/Hoodie/neutral.png"],
            ContentCatalog.TeaActionId => ["Assets/Animations/Tea/frame-0060.png"],
            _ => [],
        };
        if (paths.Length == 0) return null;
        try
        {
            var layers = paths.Select(Read).ToArray();
            int width = layers[0].PixelWidth, height = layers[0].PixelHeight;
            if (layers.Any(x => x.PixelWidth != width || x.PixelHeight != height)) return null;
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
                foreach (var layer in layers) context.DrawImage(layer, new Rect(0, 0, width, height));
            var composite = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            composite.Render(drawing); composite.Freeze();
            var result = CropAlpha(composite);
            if (result is not null) Cache[item.Id] = result;
            return result;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return null; }
    }

    internal static ImageSource? TryLoadHeader()
    {
        try { return Read("Assets/Ui/club-shop-v1.png"); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return null; }
    }

    private static BitmapSource Read(string path)
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/" + path))?.Stream
            ?? throw new FileNotFoundException("Missing shop illustration.", path);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }

    private static BitmapSource? CropAlpha(BitmapSource image)
    {
        int width = image.PixelWidth, height = image.PixelHeight, stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)]; image.CopyPixels(pixels, stride, 0);
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (pixels[y * stride + x * 4 + 3] >= 32)
                { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        if (right < left) return null;
        var result = new CroppedBitmap(image, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
        result.Freeze(); return result;
    }
}
