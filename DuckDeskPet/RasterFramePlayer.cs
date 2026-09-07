using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DuckDeskPet.Core;

namespace DuckDeskPet;

/// <summary>Predecoded, frozen PNG sequences. No decoding, blending or flips on the render path.</summary>
internal sealed class RasterFramePlayer
{
    private readonly Image _target;
    private readonly AnimationAssets _assets;
    private readonly BitmapSource _neutral;
    private readonly Dictionary<ClipKind, BitmapSource[]> _frames = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private BitmapSource? _displayed;

    public RasterFramePlayer(Image target)
    {
        _target = target;
        _assets = AnimationAssets.Load();
        _neutral = LoadBitmap(_assets.Neutral);
        _target.Source = _displayed = _neutral;
    }

    public Task WarmAsync() => WarmGroupAsync(workScene: false);
    public Task WarmWorkAsync() => WarmGroupAsync(workScene: true);
    internal int DecodedFrameCount => _frames.Values.Sum(x => x.Length);

    public void ReleaseWorkFrames()
    {
        foreach (var kind in _frames.Keys.Where(ClipCatalog.IsWorkScene).ToArray()) _frames.Remove(kind);
    }

    private async Task WarmGroupAsync(bool workScene)
    {
        await _loadGate.WaitAsync();
        try
        {
            var missing = _assets.Actions.Where(action =>
                ClipCatalog.IsWorkScene(Enum.Parse<ClipKind>(action.Clip)) == workScene &&
                !_frames.ContainsKey(Enum.Parse<ClipKind>(action.Clip))).ToArray();
            var loaded = await Task.Run(() =>
            {
                var result = new Dictionary<ClipKind, BitmapSource[]>();
                foreach (var action in missing)
                {
                    var sequence = new BitmapSource[action.FrameCount];
                    for (int i = 0; i < sequence.Length; i++)
                        sequence[i] = LoadBitmap($"{action.Directory}/frame-{i:0000}.png");
                    result.Add(Enum.Parse<ClipKind>(action.Clip), sequence);
                }
                return result;
            });
            foreach (var pair in loaded) _frames[pair.Key] = pair.Value;
        }
        finally { _loadGate.Release(); }
    }

    public void Apply(ClipSample sample)
    {
        BitmapSource source = _neutral;
        if (_frames.TryGetValue(sample.Kind, out var frames))
        {
            int index = Math.Clamp((int)Math.Round(sample.Progress * (frames.Length - 1)), 0, frames.Length - 1);
            source = frames[index];
        }
        if (ReferenceEquals(source, _displayed)) return;
        _target.Source = _displayed = source;
    }

    internal static BitmapSource LoadBitmap(string path)
    {
        using Stream stream = Application.GetResourceStream(
            new Uri($"pack://application:,,,/{path}", UriKind.Absolute))?.Stream
            ?? throw new InvalidDataException("Missing animation frame: " + path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.DecodePixelWidth = 320;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
