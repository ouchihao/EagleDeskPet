using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DuckDeskPet;

/// <summary>Short, compositor-only UI flourishes. Never owns an interaction or delays closing.</summary>
internal static class PetUiMotion
{
    private static readonly ConditionalWeakTable<FrameworkElement, Motion> Motions = new();
    private static Assembly? _applicationAssembly;
    internal static bool IsMotionEnabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    internal static void RegisterForApplication(Assembly assembly)
    {
        if (_applicationAssembly is not null) return;
        _applicationAssembly = assembly;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(WindowLoaded));
        EventManager.RegisterClassHandler(typeof(TabControl), Selector.SelectionChangedEvent, new SelectionChangedEventHandler(PageSelected));
    }

    private static bool IsCompanion(Window? window) => window is not null &&
        window.GetType().Assembly == _applicationAssembly && window.GetType().Name is not "PetWindow" and not "SpeechBubble";

    private static void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || !ReferenceEquals(e.OriginalSource, window) || !IsCompanion(window)) return;
        if (window.Content is FrameworkElement content) Reveal(content);
    }

    private static void PageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tab || !ReferenceEquals(e.OriginalSource, tab) ||
            !IsCompanion(Window.GetWindow(tab)) || e.AddedItems.Count == 0) return;
        if (tab.SelectedContent is FrameworkElement content) Reveal(content);
    }

    internal static void Reveal(FrameworkElement element, bool? motionEnabled = null)
    {
        if (!Motions.TryGetValue(element, out var motion))
        {
            motion = new Motion(element.RenderTransform, element.Opacity);
            Motions.Add(element, motion);
            element.Unloaded += (_, _) => Stop(element);
            element.IsVisibleChanged += (_, _) => { if (!element.IsVisible) Stop(element); };
        }
        Stop(element);
        if (!(motionEnabled ?? IsMotionEnabled)) return;

        // Add one transform after the existing transform, never replace a stage's own scale.
        var group = new TransformGroup();
        group.Children.Add(motion.OriginalTransform);
        var shift = new TranslateTransform();
        group.Children.Add(shift);
        element.RenderTransform = group;
        var duration = TimeSpan.FromMilliseconds(180);
        var movement = new DoubleAnimation(7, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        var opacity = new DoubleAnimation(motion.OriginalOpacity * .55, motion.OriginalOpacity, duration) { FillBehavior = FillBehavior.Stop };
        // Generation guards make rapid page changes replace, not queue, animation callbacks.
        long generation = ++motion.Generation;
        opacity.Completed += (_, _) => { if (motion.Generation == generation) Stop(element); };
        shift.BeginAnimation(TranslateTransform.YProperty, movement, HandoffBehavior.SnapshotAndReplace);
        element.BeginAnimation(UIElement.OpacityProperty, opacity, HandoffBehavior.SnapshotAndReplace);
    }

    internal static void Stop(FrameworkElement element)
    {
        if (!Motions.TryGetValue(element, out var motion)) return;
        ++motion.Generation;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.SetCurrentValue(UIElement.OpacityProperty, motion.OriginalOpacity);
        if (element.RenderTransform is TransformGroup group && group.Children.LastOrDefault() is TranslateTransform shift)
            shift.BeginAnimation(TranslateTransform.YProperty, null);
        element.RenderTransform = motion.OriginalTransform;
    }

    private sealed class Motion(Transform originalTransform, double originalOpacity)
    {
        internal Transform OriginalTransform { get; } = originalTransform;
        internal double OriginalOpacity { get; } = originalOpacity;
        internal long Generation;
    }
}
