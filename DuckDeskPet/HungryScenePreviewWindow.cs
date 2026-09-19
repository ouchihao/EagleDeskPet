using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DuckDeskPet.Core;

namespace DuckDeskPet;

/// <summary>A local, isolated show: no care service, save store, wallet, or ownership callbacks.</summary>
internal sealed class HungryScenePreviewWindow : Window
{
    private static readonly ClipKind[] HungryClips = [ClipKind.HungryEnter, ClipKind.HungryLoop, ClipKind.HungryExit];
    private readonly string _requestedOutfit;
    private readonly SceneSelection _requestedScene;
    private readonly HungrySceneTimeline _timeline = new();
    private readonly FrameRateGate _rate = new(60);
    private readonly CancellationTokenSource _loading = new();
    private readonly Image _pet = Layer(), _back = Layer(), _front = Layer(), _computer = Layer(), _fire = Layer();
    private readonly TextBlock _stageText, _warning;
    private readonly Button _pause, _replay, _finish;
    private RasterFramePlayer? _player;
    private WorkStageRenderer? _stage;
    private Task? _prepareTask;
    private double _idleSeconds;
    private bool _ready, _paused, _finishing, _closed, _subscribed;

    internal HungryScenePreviewWindow(string outfitId, SceneSelection selection)
    {
        _requestedOutfit = outfitId; _requestedScene = selection;
        Title = "大头鹰 · 空碗小剧场"; Width = 410; Height = 465; MinWidth = 350; MinHeight = 390;
        Background = Brush("#FFF9EF"); Foreground = Brush("#493827"); FontFamily = new("Microsoft YaHei UI");
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/PetTheme.xaml") });
        var root = new Grid { Margin = new(20), Background = Background };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "饭呢？我的饭呢？", FontSize = 22, FontWeight = FontWeights.Bold });
        header.Children.Add(new TextBlock { Text = "空碗小剧场 · 只看表演，不改变桌宠", FontSize = 11, Foreground = Brush("#7D856B"), Margin = new(0, 6, 0, 8) });
        _warning = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#A47839"), Margin = new(0, 0, 0, 8) };
        header.Children.Add(_warning); root.Children.Add(header);
        var layers = new Grid { Width = 302, Height = 265, ClipToBounds = true, Background = Brush("#F1EFE5") };
        foreach (var image in new[] { _fire, _back, _pet, _front, _computer }) layers.Children.Add(image);
        var viewport = new Viewbox { Child = layers, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        Grid.SetRow(viewport, 1); root.Children.Add(viewport);
        var footer = new StackPanel { Margin = new(0, 12, 0, 0) };
        _stageText = new() { Text = "正在准备完整的小剧场…", FontSize = 11, Foreground = Brush("#80725C"), TextWrapping = TextWrapping.Wrap };
        footer.Children.Add(_stageText);
        footer.Children.Add(new TextBlock { Text = "不扣粮、不花鹰币、不领奖，也不会更改装备。", FontSize = 10, Foreground = Brush("#99917B"), Margin = new(0, 5, 0, 10), TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel();
        _pause = Button("暂停", () => { _paused = !_paused; UpdateControls(); UpdateClock(); });
        _replay = Button("再看一遍", Replay);
        _finish = Button("收场", Finish);
        var close = Button("关掉小剧场", Close);
        foreach (var button in new[] { _pause, _replay, _finish, close }) buttons.Children.Add(button);
        footer.Children.Add(buttons); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        _pause.IsEnabled = _replay.IsEnabled = _finish.IsEnabled = false;
        Activated += (_, _) => UpdateClock(); Deactivated += (_, _) => UpdateClock();
        IsVisibleChanged += (_, _) => UpdateClock(); StateChanged += (_, _) => UpdateClock();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        Closed += (_, _) => Release();
    }

    internal bool IsPrepared => _ready && !_closed;
    internal bool IsRenderLoopAttached => _subscribed;
    internal bool CanReplay => IsPrepared && !_timeline.IsActive && _idleSeconds >= 2;
    internal int RetainedFrameCount => _player?.DecodedFrameCount ?? 0;
    internal string? EffectiveOutfitId => _player?.CurrentOutfitId;
    internal SceneSelection? EffectiveSelection => _stage?.ActiveSelection;
    internal ClipSample CurrentSample => _timeline.CurrentSample;
    internal string WarningText => _warning.Text;
    internal Task PrepareAsync() => _closed ? Task.CompletedTask : _prepareTask ??= PrepareCoreAsync();

    private async Task PrepareCoreAsync()
    {
        try
        {
            var stage = _stage = new WorkStageRenderer(_back, _front, _computer, _fire, character: _pet);
            var player = _player = new RasterFramePlayer(_pet);
            var warnings = new List<string>();
            if (!string.Equals(_requestedOutfit, OutfitCatalog.DefaultId, StringComparison.Ordinal))
            {
                var result = await player.SelectOutfitAsync(_requestedOutfit, _loading.Token);
                if (_closed) return;
                if (!result.Success) warnings.Add("所选装扮暂不可用，本次只预览原味大头鹰；已保存的装备不会改变。");
            }
            // Default previews decode only the three required clips. Outfit changes retain the player's
            // strict whole-suite validation; they never mix a default head with another suit mid-scene.
            await player.WarmClipsAsync(HungryClips, _loading.Token);
            if (_closed) return;
            if (HungryClips.Any(kind => !player.IsClipReady(kind))) throw new InvalidDataException("Hungry preview frames did not finish loading.");
            if (stage.Catalog.TryResolve(_requestedScene, out _, out _))
            {
                try { await stage.RequestSelectionAsync(_requestedScene); }
                catch (Exception ex) when (OutfitCatalog.IsResourceError(ex))
                { warnings.Add("所选桌子暂不可用，本次只预览经典木桌；已保存的装备不会改变。"); }
            }
            else warnings.Add("所选工位暂不可用，本次只预览经典木桌；已保存的装备不会改变。");
            if (_closed) return;
            stage.Apply(ClipSample.Idle);
            _warning.Text = string.Join("\n", warnings);
            _ready = true; _timeline.Start(); ApplyFrame(); UpdateControls(); UpdateClock();
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closed)
            {
                _warning.Text = "小剧场素材未能完整加载，暂时无法播放。桌宠、装备和所有奖励均未改变。";
                _stageText.Text = "没有使用缺帧或静止图片代替表演。";
                ReleasePlayers();
            }
            throw;
        }
    }

    private void Replay()
    {
        if (!CanReplay) return;
        _paused = _finishing = false; _idleSeconds = 0; _timeline.Start(_timeline.CurrentSample.Sequence);
        ApplyFrame(); UpdateControls(); UpdateClock();
    }
    private void Finish()
    {
        if (!IsPrepared || !_timeline.IsActive || _finishing) return;
        _finishing = true; _paused = false; _timeline.Cancel(); UpdateControls(); UpdateClock();
    }
    private void UpdateClock()
    {
        bool run = IsPrepared && !_paused && (_timeline.IsActive || _idleSeconds < 2) &&
            IsVisible && IsActive && WindowState != WindowState.Minimized;
        if (run && !_subscribed) { _rate.Reset(); CompositionTarget.Rendering += OnRendering; _subscribed = true; }
        else if (!run && _subscribed) { CompositionTarget.Rendering -= OnRendering; _subscribed = false; _rate.Reset(); }
    }
    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs render && _rate.TryAdvance(render.RenderingTime.TotalSeconds, out double delta)) AdvanceTimeline(delta);
    }
    private void AdvanceTimeline(double deltaSeconds)
    {
        if (!IsPrepared || _paused) return;
        if (_timeline.IsActive) _timeline.Advance(deltaSeconds);
        else _idleSeconds = Math.Min(2, _idleSeconds + deltaSeconds);
        ApplyFrame(); UpdateControls(); UpdateClock();
    }
    private void ApplyFrame()
    { _player?.Apply(_timeline.CurrentSample); _stage?.Apply(_timeline.CurrentSample); }
    private void UpdateControls()
    {
        _pause.Content = _paused ? "继续" : "暂停";
        _pause.IsEnabled = IsPrepared && (_timeline.IsActive || _idleSeconds < 2);
        _replay.IsEnabled = CanReplay; _finish.IsEnabled = IsPrepared && _timeline.IsActive && !_finishing;
        string phase = _timeline.CurrentSample.Kind switch
        {
            ClipKind.HungryEnter => "进场 · 搬来桌子，掏出空碗…",
            ClipKind.HungryLoop => $"空碗找饭中 · 第 {_timeline.CompletedLoopCount + 1} / {HungrySceneTimeline.MaximumLoopCount} 段",
            ClipKind.HungryExit => "收场 · 放下空碗，把桌子收好。",
            _ => CanReplay ? "小剧场演完啦，可以再看一遍。" : "站稳 2 秒，给小鹰一个喘气的间隙。",
        };
        _stageText.Text = _paused ? "已暂停 · 继续时从当前这一帧接着演。" : _finishing && _timeline.CurrentSample.Kind != ClipKind.HungryExit && _timeline.IsActive
            ? "已安排收场，先演完当前片段，再完整退场。" : phase;
    }
    private void Release()
    {
        if (_closed) return;
        _closed = true; _ready = false; _loading.Cancel(); UpdateClock(); ReleasePlayers();
        // Do not dispose a token source while its asynchronous loader still owns the token.
        if (_prepareTask is null || _prepareTask.IsCompleted) _loading.Dispose();
        else _ = _prepareTask.ContinueWith(_ => _loading.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private void ReleasePlayers()
    {
        _player?.Dispose(); _player = null;
        _stage?.ReleaseFireFrames(); _stage = null;
        foreach (var image in new[] { _pet, _back, _front, _computer, _fire }) image.Source = null;
    }
    private Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Style = (Style)FindResource("PetButton"), Padding = new(11, 7, 11, 7), Margin = new(0, 0, 7, 5) };
        button.Click += (_, _) => action(); return button;
    }
    private static Image Layer() => new() { Width = 256, Height = 232, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private static SolidColorBrush Brush(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
}
