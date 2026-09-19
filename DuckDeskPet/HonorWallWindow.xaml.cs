using System.Windows;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class HonorWallWindow : Window
{
    private readonly Func<PetState> _stateProvider;
    private readonly Action<bool>? _openShop;
    private readonly Func<string, string>? _claimReward;
    private readonly Func<ContentDefinition, bool>? _contentAvailable;
    private string? _lastContentSignature;
    private IReadOnlyList<HonorProgress>? _lastSnapshot;
    private bool _ready;
    private readonly Dictionary<FrameworkElement, MedalMotion> _medalMotions = new();
    private readonly DispatcherTimer _sheenTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(7.2) };
    private int _sheenIndex;
    private bool _closed;

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(HonorWallWindow), new PropertyMetadata(254.0));
    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        private set => SetValue(CardWidthProperty, value);
    }

    internal HonorWallWindow(Func<PetState> stateProvider, Action<bool>? openShop = null,
        Func<string, string>? claimReward = null, Func<ContentDefinition, bool>? contentAvailable = null)
    {
        _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        _openShop = openShop; _claimReward = claimReward; _contentAvailable = contentAvailable;
        InitializeComponent();
        ShopButton.IsEnabled = CollectionButton.IsEnabled = openShop is not null;
        // Work-area units are already DPI-independent; keep native chrome,
        // resizing and keyboard behavior on small screens and high-DPI displays.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(360, area.Width - 32));
        Height = Math.Min(Height, Math.Max(360, area.Height - 32));
        _ready = true;
        Refresh();
        Loaded += (_, _) =>
        {
            KeepInsideWorkArea();
            UpdateCardWidth();
            UpdateAnimationActivity();
        };
        _sheenTimer.Tick += SheenTimer_OnTick;
        IsVisibleChanged += (_, _) => UpdateAnimationActivity();
        StateChanged += (_, _) => UpdateAnimationActivity();
        Activated += (_, _) => UpdateAnimationActivity();
        Deactivated += (_, _) => UpdateAnimationActivity();
        CardScroll.ScrollChanged += (_, _) => UpdateAnimationActivity();
        SystemParameters.StaticPropertyChanged += SystemParameters_OnChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _sheenTimer.Stop();
            _sheenTimer.Tick -= SheenTimer_OnTick;
            SystemParameters.StaticPropertyChanged -= SystemParameters_OnChanged;
            foreach (var motion in _medalMotions.Values) motion.Stop();
            _medalMotions.Clear();
        };
    }

    internal void Refresh()
    {
        var state = _stateProvider();
        IReadOnlyList<HonorProgress> snapshot = HonorCatalog.Evaluate(state);
        string contentSignature = string.Join("|", ContentCatalog.Definitions.Where(x => x.Reward is not null)
            .Select(x => $"{x.Id}:{ContentOwnershipService.Owns(state.Content, x.Id)}:{ContentAvailable(x)}"));
        if (_lastSnapshot is not null && snapshot.SequenceEqual(_lastSnapshot) && contentSignature == _lastContentSignature) return;
        _lastSnapshot = snapshot;
        _lastContentSignature = contentSignature;
        CountText.Text = $"已收藏 {snapshot.Count(x => x.IsEarned)} / {snapshot.Count}";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_lastSnapshot is null) return;
        var cards = _lastSnapshot
            .Where(x => EarnedFilter.IsChecked == true ? x.IsEarned : LockedFilter.IsChecked != true || !x.IsEarned)
            .Select(x => new HonorCard(x, _stateProvider(), ContentAvailable, _claimReward is not null, _openShop is not null)).ToArray();
        CardsItems.ItemsSource = cards;
        UpdateAnimationActivity();
        EmptyText.Visibility = cards.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = EarnedFilter.IsChecked == true
            ? "第一枚徽章还在等你。给小鹰喂一口饭，先拿一个开门红。"
            : "九枚全亮！本鹰宣布：你是这个桌面的荣誉收藏家。";
    }

    private void Filter_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_ready) ApplyFilter();
    }

    private void Window_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_ready) UpdateCardWidth();
    }

    private void UpdateCardWidth()
    {
        double available = Math.Max(260, CardScroll.ActualWidth - 51);
        int columns = available >= 705 ? 3 : available >= 470 ? 2 : 1;
        CardWidth = Math.Floor(available / columns);
    }

    private void KeepInsideWorkArea()
    {
        // A tiny desktop-pet owner may sit against a screen edge. CenterOwner
        // alone can place half the exhibit off-screen; clamp the complete native
        // window rectangle on its actual monitor, including mixed-DPI displays.
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero || !NativeMethods.GetWindowRect(handle, out var window)) return;
        nint monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };
        if (monitor == nint.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        int width = Math.Min(window.Width, info.Work.Width);
        int height = Math.Min(window.Height, info.Work.Height);
        int left = Math.Clamp(window.Left, info.Work.Left, info.Work.Right - width);
        int top = Math.Clamp(window.Top, info.Work.Top, info.Work.Bottom - height);
        NativeMethods.SetWindowPos(handle, nint.Zero, left, top, width, height, NativeMethods.SwpNoActivate);
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
    private void Shop_OnClick(object sender, RoutedEventArgs e) => _openShop?.Invoke(false);
    private void Collection_OnClick(object sender, RoutedEventArgs e) => _openShop?.Invoke(true);
    private void Reward_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HonorCard card } || card.RewardContentId is null) return;
        var item = ContentCatalog.Get(card.RewardContentId); var state = _stateProvider();
        if (ContentOwnershipService.Owns(state.Content, item.Id)) { _openShop?.Invoke(true); return; }
        if (!ContentOwnershipService.IsRewardEligible(item, state) || _claimReward is null) return;
        RewardStatusText.Text = _claimReward(item.Id);
        Refresh();
    }
    private bool ContentAvailable(ContentDefinition item)
    {
        if (_contentAvailable is null) return true; // Older standalone galleries have no resource authority.
        try { return _contentAvailable(item); } catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    // No CompositionTarget.Rendering subscription: short, compositor-driven sweeps
    // belong only to the open foreground gallery, not the always-running pet.
    private bool AnimationsAllowed => !_closed && IsLoaded && IsVisible && IsActive
        && WindowState != WindowState.Minimized && SystemParameters.ClientAreaAnimation;

    private void SystemParameters_OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
        if (Dispatcher.CheckAccess()) UpdateAnimationActivity();
        else Dispatcher.BeginInvoke(UpdateAnimationActivity);
    }

    private bool CanAnimate(FrameworkElement host)
    {
        if (!AnimationsAllowed || host.DataContext is not HonorCard { IsEarned: true } || !host.IsVisible) return false;
        try
        {
            Rect bounds = host.TransformToAncestor(CardScroll).TransformBounds(new Rect(host.RenderSize));
            return bounds.IntersectsWith(new Rect(0, 0, CardScroll.ActualWidth, CardScroll.ActualHeight));
        }
        catch (InvalidOperationException) { return false; } // A filter just detached this template.
    }

    private void UpdateAnimationActivity()
    {
        bool any = false;
        foreach (var (host, motion) in _medalMotions)
        {
            if (CanAnimate(host)) any = true;
            else motion.Stop();
        }
        if (any) _sheenTimer.Start();
        else _sheenTimer.Stop();
    }

    private void SheenTimer_OnTick(object? sender, EventArgs e)
    {
        UpdateAnimationActivity();
        var visible = _medalMotions.Where(x => CanAnimate(x.Key)).Select(x => x.Value).ToArray();
        if (visible.Length > 0) visible[_sheenIndex++ % visible.Length].Sweep();
    }

    private void Medal_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_closed || sender is not Grid host || _medalMotions.ContainsKey(host)) return;
        _medalMotions.Add(host, new MedalMotion(host));
        UpdateAnimationActivity();
    }

    private void Medal_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement host && _medalMotions.Remove(host, out var motion)) motion.Stop();
        UpdateAnimationActivity();
    }

    private void Medal_OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement host && CanAnimate(host) && _medalMotions.TryGetValue(host, out var motion))
            motion.Hover(true);
    }

    private void Medal_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement host && _medalMotions.TryGetValue(host, out var motion))
        {
            if (CanAnimate(host)) motion.Hover(false);
            else motion.Stop();
        }
    }

    private sealed class MedalMotion
    {
        private readonly ScaleTransform _scale;
        private readonly TranslateTransform _lift;
        private readonly Rectangle _sheen;
        private readonly TranslateTransform _travel;
        private bool _sweeping;

        public MedalMotion(Grid host)
        {
            // DataTemplate freezables can be shared/frozen. Every medal needs its
            // own mutable transforms; animating the template instance can throw.
            var transform = (TransformGroup)host.RenderTransform.CloneCurrentValue();
            host.RenderTransform = transform;
            _scale = (ScaleTransform)transform.Children[0];
            _lift = (TranslateTransform)transform.Children[1];
            _sheen = ((Grid)host.Children[0]).Children.OfType<Rectangle>().Single();
            _sheen.RenderTransform = _sheen.RenderTransform.CloneCurrentValue();
            _travel = (TranslateTransform)((TransformGroup)_sheen.RenderTransform).Children[1];
        }

        public void Hover(bool enter)
        {
            AnimateTo(_scale, ScaleTransform.ScaleXProperty, enter ? 1.025 : 1);
            AnimateTo(_scale, ScaleTransform.ScaleYProperty, enter ? 1.025 : 1);
            AnimateTo(_lift, TranslateTransform.YProperty, enter ? -2.5 : 0);
            if (enter) Sweep();
        }

        private static void AnimateTo(Animatable target, DependencyProperty property, double value)
        {
            double current = (double)target.GetValue(property);
            target.BeginAnimation(property, null);
            target.SetValue(property, value);
            var animation = new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };
            animation.Completed += (_, _) => target.BeginAnimation(property, null);
            target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }

        public void Sweep()
        {
            if (_sweeping) return;
            _sweeping = true;
            var travel = new DoubleAnimation(-190, 190, TimeSpan.FromMilliseconds(1150)) { FillBehavior = FillBehavior.Stop };
            travel.Completed += (_, _) =>
            {
                _travel.BeginAnimation(TranslateTransform.XProperty, null);
                _sheen.BeginAnimation(UIElement.OpacityProperty, null);
                _sweeping = false;
            };
            var opacity = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.23, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.23, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1150))));
            _sheen.BeginAnimation(UIElement.OpacityProperty, opacity, HandoffBehavior.SnapshotAndReplace);
            _travel.BeginAnimation(TranslateTransform.XProperty, travel, HandoffBehavior.SnapshotAndReplace);
        }

        public void Stop()
        {
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _lift.BeginAnimation(TranslateTransform.YProperty, null);
            _travel.BeginAnimation(TranslateTransform.XProperty, null);
            _sheen.BeginAnimation(UIElement.OpacityProperty, null);
            _scale.ScaleX = _scale.ScaleY = 1;
            _lift.Y = 0;
            _travel.X = -190;
            _sheen.Opacity = 0;
            _sweeping = false;
        }
    }

    private sealed class HonorCard
    {
        public HonorCard(HonorProgress progress, PetState state, Func<ContentDefinition, bool> isAvailable,
            bool canClaim, bool canOpenCollection)
        {
            var definition = progress.Definition;
            Name = definition.Name;
            Description = definition.Description;
            TierLabel = definition.Tier switch { HonorTier.Gold => "金级", HonorTier.Silver => "银级", _ => "铜级" };
            TierTint = Brush(definition.Tier switch { HonorTier.Gold => "#F1E0A6", HonorTier.Silver => "#E1E5E5", _ => "#EED6BF" });
            string series = definition.Series switch { HonorSeries.Meals => "meals", HonorSeries.Affection => "affection", _ => "growth" };
            BadgeUri = $"pack://application:,,,/EagleDeskPet;component/Assets/Badges/{series}-{definition.Tier.ToString().ToLowerInvariant()}-v2.png";
            SeriesLabel = definition.Series switch { HonorSeries.Meals => "干饭搭子", HonorSeries.Affection => "摸头交情", _ => "成长足迹" };
            RimBrush = Brush(definition.Tier switch { HonorTier.Gold => "#B18B35", HonorTier.Silver => "#8F979F", _ => "#9E7045" });
            ProgressLabel = definition.Series == HonorSeries.Growth
                ? $"Lv.{progress.Current} / Lv.{definition.Target}"
                : $"{progress.Current} / {definition.Target} {definition.Unit}";
            ProgressPercent = progress.Fraction * 100;
            IsEarned = progress.IsEarned;
            BadgeOpacity = progress.IsEarned ? 1.0 : 0.56;
            LockedVisibility = progress.IsEarned ? Visibility.Collapsed : Visibility.Visible;
            StatusLabel = progress.IsEarned ? "✓ 已获得" : "待解锁";
            StatusBrush = Brush(progress.IsEarned ? "#677D5D" : "#A19680");
            BadgeLabel = $"大头鹰{TierLabel}浮雕纪念币 · {SeriesLabel}";
            AccessibleLabel = $"{Name}，{BadgeLabel}，{StatusLabel}，{ProgressLabel}。{Description}";
            var reward = ContentCatalog.Definitions.FirstOrDefault(x => x.Reward?.HonorId == definition.Id);
            RewardContentId = reward?.Id;
            RewardVisibility = reward is null ? Visibility.Collapsed : Visibility.Visible;
            if (reward is not null)
            {
                bool owned = ContentOwnershipService.Owns(state.Content, reward.Id);
                RewardLabel = owned ? $"关联奖励：{reward.Name} · 已拥有，不重复发放"
                    : progress.IsEarned ? $"可免费领取：{reward.Name}" : $"达成奖励：{reward.Name}";
                if (!isAvailable(reward)) RewardLabel += "\n资源暂未就绪，领取权益仍会保留。";
                RewardActionLabel = owned ? "查看收藏" : "领取奖励";
                CanUseReward = owned ? canOpenCollection : progress.IsEarned && canClaim;
            }
        }

        public string Name { get; }
        public string Description { get; }
        public string TierLabel { get; }
        public string SeriesLabel { get; }
        public string BadgeUri { get; }
        public string BadgeLabel { get; }
        public string ProgressLabel { get; }
        public string StatusLabel { get; }
        public string AccessibleLabel { get; }
        public string? RewardContentId { get; }
        public string RewardLabel { get; } = "";
        public string RewardActionLabel { get; } = "";
        public bool CanUseReward { get; }
        public Visibility RewardVisibility { get; }
        public Brush TierTint { get; }
        public Brush RimBrush { get; }
        public Brush StatusBrush { get; }
        public bool IsEarned { get; }
        public double ProgressPercent { get; }
        public double BadgeOpacity { get; }
        public Visibility LockedVisibility { get; }

        private static Brush Brush(string hex)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
            brush.Freeze();
            return brush;
        }
    }
}
