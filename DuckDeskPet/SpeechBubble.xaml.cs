using System.Windows;
using System.Windows.Interop;

namespace DuckDeskPet;

public partial class SpeechBubble : Window
{
    private readonly Action _open;
    private readonly Action _dismiss;
    internal SpeechBubble(Action open, Action dismiss)
    {
        InitializeComponent();
        _open = open;
        _dismiss = dismiss;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle,
                NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle) | (nint)NativeMethods.WsExToolWindow);
        };
    }
    internal void SetMessage(string source, string message, string quip, bool canOpen)
    {
        // AI identity belongs in the sentence, not a separate pet-name/header row.
        // Everyday chatter deliberately ignores the old "大头鹰 · 碎碎念" source.
        var text = canOpen && !string.IsNullOrWhiteSpace(source)
            ? $"{source.Trim()}。{message.Trim()}"
            : message.Trim();
        if (!string.IsNullOrWhiteSpace(quip)) text += $" {quip.Trim()}";
        MessageText.Text = text;
        MessageText.ToolTip = text;
        OpenButton.Visibility = canOpen ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Open_OnClick(object sender, RoutedEventArgs e) => _open();
    private void Close_OnClick(object sender, RoutedEventArgs e) => _dismiss();
}
