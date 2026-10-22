using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DuckDeskPet.Controls;

public enum QuickAccessDestination
{
    Shop,
    Notebook,
    Timer,
    Home,
    Honors
}

public partial class QuickAccessBar : UserControl
{
    private static readonly DependencyPropertyDescriptor PressedProperty =
        DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(Button));

    public QuickAccessBar()
    {
        InitializeComponent();
        ActionButtons = Array.AsReadOnly(new[] { ShopButton, NotebookButton, TimerButton, HomeButton, HonorsButton });
        foreach (var button in ActionButtons)
        {
            button.MouseEnter += OnButtonStateChanged;
            button.MouseLeave += OnButtonStateChanged;
        }
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event Action<QuickAccessDestination>? ActionRequested;

    public IReadOnlyList<Button> ActionButtons { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        foreach (var button in ActionButtons)
        {
            PressedProperty.AddValueChanged(button, OnButtonStateChanged);
            SetButtonMotion(button, button.IsMouseOver, button.IsPressed, false);
        }
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        foreach (var button in ActionButtons)
        {
            PressedProperty.RemoveValueChanged(button, OnButtonStateChanged);
            SetButtonMotion(button, false, false, false);
        }
    }

    private void OnButtonStateChanged(object? sender, EventArgs e)
    {
        if (sender is Button button)
            SetButtonMotion(button, button.IsMouseOver, button.IsPressed, true);
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
            foreach (var button in ActionButtons)
                SetButtonMotion(button, button.IsMouseOver, button.IsPressed, false);
    }

    internal static void SetButtonMotion(Button button, bool hovered, bool pressed, bool animate)
    {
        button.ApplyTemplate();
        if (button.Template.FindName("MotionScale", button) is not ScaleTransform scale ||
            button.Template.FindName("MotionLift", button) is not TranslateTransform lift)
            return;

        animate &= SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        var targetScale = pressed ? 0.96 : hovered ? 1.11 : 1;
        var targetLift = hovered && !pressed ? -1 : 0;
        AnimateTo(scale, ScaleTransform.ScaleXProperty, targetScale, animate);
        AnimateTo(scale, ScaleTransform.ScaleYProperty, targetScale, animate);
        AnimateTo(lift, TranslateTransform.YProperty, targetLift, animate);
    }

    private static void AnimateTo(Animatable target, DependencyProperty property, double value, bool animate)
    {
        var current = (double)target.GetValue(property);
        target.SetValue(property, value);
        target.BeginAnimation(property, animate && current != value
            ? new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            }
            : null, HandoffBehavior.SnapshotAndReplace);
    }

    private void Action_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: QuickAccessDestination destination })
            ActionRequested?.Invoke(destination);
    }
}
