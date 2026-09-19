using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuckDeskPet.Core;

namespace DuckDeskPet;

/// <summary>Own visual tree, decoded frames and clock. Never receives care, wallet or ownership services.</summary>
internal sealed class ContentPreviewWindow : Window
{
    private readonly ContentDefinition _item;
    private readonly SceneSelection _selection;
    private readonly Func<ContentDefinition, bool> _available;
    private readonly Image _pet;
    private readonly WorkStageRenderer _stage;
    private readonly TextBlock _status;
    private readonly Button _pause;
    private readonly CancellationTokenSource _loading = new();
    private readonly Stopwatch _clock = new();
    private IReadOnlyList<BitmapSource>? _frames;
    private BitmapSource? _neutral;
    private ClipKind _clip = ClipKind.Idle;
    private double _duration;
    private bool _scene, _ready, _closed, _subscribed, _paused;

    internal ContentPreviewWindow(ContentDefinition item, SceneSelection selection, Func<ContentDefinition, bool> available)
    {
        _item = item; _selection = selection; _available = available;
        Title = "大头鹰 · " + item.Name + "预览";
        Width = 380; Height = 425; MinWidth = 340; MinHeight = 390;
        Background = Brush("#FFF9EF"); Foreground = Brush("#493827");
        FontFamily = new FontFamily("Microsoft YaHei UI"); ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/PetTheme.xaml") });
        var root = new Grid { Margin = new Thickness(20), Background = Background };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = item.Name, FontSize = 21, FontWeight = FontWeights.Bold });
        heading.Children.Add(new TextBlock { Text = "独立小舞台 · 不扣款、不领奖、不换装", FontSize = 10, Foreground = Brush("#85836C"), Margin = new(0, 6, 0, 10) });
        root.Children.Add(heading);
        var scene = new Grid { Width = 290, Height = 265, ClipToBounds = true, Background = Brush("#F1EFE5") };
        var fire = Layer(); var back = Layer(); _pet = Layer(); var front = Layer(); var computer = Layer();
        foreach (var image in new[] { fire, back, _pet, front, computer }) scene.Children.Add(image);
        // A short/narrow window scales this self-contained stage instead of cropping feet or controls.
        var viewport = new Viewbox { Child = scene, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        Grid.SetRow(viewport, 1); root.Children.Add(viewport);
        _stage = new WorkStageRenderer(back, front, computer, fire);
        var footer = new StackPanel { Margin = new(0, 12, 0, 0) };
        _status = new TextBlock { Text = "正在准备预览…", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#81745F"), Margin = new(0, 0, 0, 10) };
        footer.Children.Add(_status);
        var controls = new DockPanel();
        var close = new Button { Content = "看完啦", Style = (Style)FindResource("PetButton"), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); controls.Children.Add(close);
        _pause = new Button { Content = "暂停预览", Style = (Style)FindResource("PetButton"), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };
        _pause.Click += (_, _) => { _paused = !_paused; _pause.Content = _paused ? "继续预览" : "暂停预览"; UpdateClock(); };
        controls.Children.Add(_pause); footer.Children.Add(controls); Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = root;
        Activated += (_, _) => UpdateClock(); Deactivated += (_, _) => UpdateClock(); IsVisibleChanged += (_, _) => UpdateClock();
        StateChanged += (_, _) => UpdateClock();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        Closed += (_, _) =>
        {
            _closed = true; _loading.Cancel(); UpdateClock(); _frames = null; _neutral = null; _pet.Source = null;
            _stage.ReleaseFireFrames(); _loading.Dispose();
        };
    }

    internal async Task PrepareAsync()
    {
        if (_ready || _closed) return;
        try
        {
            if (!_available(_item)) throw new InvalidDataException("Content resources are not available.");
            CancellationToken token = _loading.Token;
            if (_item.Type == ContentType.Outfit)
            {
                _clip = ClipKind.Yawn;
                var catalog = OutfitCatalog.Load();
                _frames = await RasterFramePlayer.LoadOutfitPreviewFramesAsync(catalog, _item.OutfitId!, _clip, token);
                _neutral = (await RasterFramePlayer.LoadOutfitPreviewFramesAsync(catalog, _item.OutfitId!, ClipKind.Idle, token))[0];
                _duration = ClipCatalog.GetDefinition(_clip).DurationSeconds;
            }
            else
            {
                var assets = AnimationAssets.Load();
                _scene = _item.Type is ContentType.Desk or ContentType.Computer;
                var action = assets.Actions.FirstOrDefault(x => _scene ? x.Clip == nameof(ClipKind.WorkLoop) : x.Id == _item.AnimationId)
                    ?? throw new InvalidDataException("Missing content preview animation.");
                _clip = Enum.Parse<ClipKind>(action.Clip); _duration = action.DurationSeconds;
                var frames = await Task.Run(() => Enumerable.Range(0, action.FrameCount).Select(i =>
                {
                    token.ThrowIfCancellationRequested();
                    return RasterFramePlayer.LoadBitmap($"{action.Directory}/frame-{i:0000}.png");
                }).ToArray(), token);
                _frames = frames;
                _neutral = RasterFramePlayer.LoadBitmap(assets.Neutral);
                if (_scene)
                {
                    var selected = _item.Type == ContentType.Desk ? _selection with { DeskId = _item.ScenePropId! }
                        : _selection with { ComputerId = _item.ScenePropId! };
                    await _stage.RequestSelectionAsync(selected);
                    _stage.Apply(ClipSample.Idle);
                }
            }
            if (_closed) { _frames = null; _neutral = null; return; }
            _ready = true; _pause.IsEnabled = true;
            _status.Text = _scene ? "持续办公的实际图层预览；关闭后桌宠保持原样。" : "完整动作后站立 2 秒，再从头预览。";
            RenderFrame(); UpdateClock();
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closed) _status.Text = "素材暂未完整加载。未扣款，也没有改变桌宠或收藏。";
            throw;
        }
    }

    private void UpdateClock()
    {
        bool run = _ready && !_closed && !_paused && IsVisible && IsActive && WindowState != WindowState.Minimized;
        if (run && !_subscribed) { _clock.Start(); CompositionTarget.Rendering += OnRendering; _subscribed = true; }
        else if (!run && _subscribed) { _clock.Stop(); CompositionTarget.Rendering -= OnRendering; _subscribed = false; }
    }
    private void OnRendering(object? sender, EventArgs e) => RenderFrame();
    private void RenderFrame()
    {
        if (_frames is not { Count: > 0 }) return;
        double local = _clock.Elapsed.TotalSeconds % (_duration + (_scene ? 0 : 2));
        bool inAction = local < _duration;
        if (!inAction) { _pet.Source = _neutral; _stage.Apply(ClipSample.Idle); return; }
        double progress = Math.Clamp(local / _duration, 0, 1);
        _pet.Source = _frames[Math.Clamp((int)Math.Round(progress * (_frames.Count - 1)), 0, _frames.Count - 1)];
        _stage.Apply(_scene ? new ClipSample(_clip, ClipPlaybackPhase.Playing, progress, 1) : ClipSample.Idle);
    }
    private static Image Layer() => new() { Width = 256, Height = 232, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private static SolidColorBrush Brush(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
}
