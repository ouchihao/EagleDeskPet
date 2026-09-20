using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DuckDeskPet;

/// <summary>Decorative, code-native scenery only. No model, clock, or actor/prop transforms.</summary>
internal static class PreviewStageChrome
{
    internal static Brush Paper => Brush("#FFF8EB");
    internal static Brush Ink => Brush("#24364B");
    internal static Brush Muted => Brush("#707B83");

    internal static FrameworkElement Ticket(string title, string subtitle, string number)
    {
        var ticket = new Grid { Margin = new(0, 0, 0, 12) };
        var body = new Border { Background = Ink, CornerRadius = new(16), Padding = new(17, 12, 17, 13) };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = "EAGLE CLUB  /  只看不买票", FontSize = 9, Foreground = Brush("#EAB85E") });
        text.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.Bold, Foreground = Paper, Margin = new(0, 3, 37, 4), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = subtitle, FontSize = 10, Foreground = Brush("#D4DCCF"), TextWrapping = TextWrapping.Wrap });
        body.Child = text; ticket.Children.Add(body);
        var serial = new TextBlock { Text = number, FontSize = 9, Foreground = Brush("#EAB85E"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 17, 15, 0) };
        ticket.Children.Add(serial);
        foreach (var align in new[] { HorizontalAlignment.Left, HorizontalAlignment.Right })
            ticket.Children.Add(new Ellipse { Width = 11, Height = 11, Fill = Paper, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center, Margin = align == HorizontalAlignment.Left ? new(-5, 0, 0, 0) : new(0, 0, -5, 0), IsHitTestVisible = false });
        return ticket;
    }

    internal static Viewbox Stage(Grid actorLayers)
    {
        // The supplied layer grid and every layer's size stay untouched. Only an
        // outer Viewbox scales the complete show as a single unit on small windows.
        double width = actorLayers.Width + 28, height = actorLayers.Height + 26;
        var theatre = new Grid { Width = width, Height = height, ClipToBounds = true, IsHitTestVisible = false };
        theatre.Children.Add(new Border { Background = new LinearGradientBrush(Color("#DDECE1"), Color("#F5EDCE"), 90), BorderBrush = Brush("#D5DAC7"), BorderThickness = new(1), CornerRadius = new(17) });
        var ornaments = new Canvas { Width = width, Height = height, IsHitTestVisible = false };
        var spotlight = new Polygon { Points = new PointCollection([new(width * .38, 0), new(width * .62, 0), new(width * .88, height - 25), new(width * .12, height - 25)]), Fill = Brush("#44FFFDF0") };
        ornaments.Children.Add(spotlight);
        var floor = new Border { Width = width, Height = 40, Background = Brush("#D8C092"), BorderBrush = Brush("#BCA476"), BorderThickness = new(0, 2, 0, 0) };
        Canvas.SetTop(floor, height - 37); ornaments.Children.Add(floor);
        var footlights = new Border { Width = width, Height = 11, Background = Ink };
        Canvas.SetTop(footlights, height - 11); ornaments.Children.Add(footlights);
        for (int i = 0; i < 11; i++)
        {
            var bulb = new Ellipse { Width = 3, Height = 3, Fill = Brush("#EAB85E") };
            Canvas.SetLeft(bulb, 19 + i * (width - 41) / 10); Canvas.SetTop(bulb, height - 7); ornaments.Children.Add(bulb);
        }
        // Side curtains stay outside the actor grid, never covering the eagle,
        // the table's authored entry path, or the registered rendering layers.
        foreach (bool right in new[] { false, true })
        {
            var curtain = new Polygon { Points = right
                ? new([new(width - 14, 0), new(width, 0), new(width, 146), new(width - 6, 91)])
                : new([new(0, 0), new(14, 0), new(6, 91), new(0, 146)]), Fill = Ink };
            ornaments.Children.Add(curtain);
            var tie = new Border { Width = 11, Height = 3, Background = Brush("#EAB85E"), CornerRadius = new(1) };
            Canvas.SetLeft(tie, right ? width - 11 : 0); Canvas.SetTop(tie, 88); ornaments.Children.Add(tie);
        }
        theatre.Children.Add(ornaments);
        theatre.Children.Add(actorLayers);
        return new Viewbox { Child = theatre, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    }

    private static Color Color(string value) => (Color)ColorConverter.ConvertFromString(value);
    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush(Color(value)); brush.Freeze(); return brush;
    }
}
