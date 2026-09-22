using System.Windows;

namespace DuckDeskPet;

public partial class PetWindow
{
    private NotebookWindow? _notebookWindow;

    private void NotebookMenu_OnClick(object sender, RoutedEventArgs e) => OpenNotebookWindow();

    internal void OpenNotebookWindow()
    {
        if (_notebookWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new NotebookWindow(AppPaths.DataDirectory) { Owner = this, Topmost = Topmost };
        _notebookWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_notebookWindow, window)) _notebookWindow = null;
        };
        window.Show();
        PlaceCompanion(window, above: false);
    }

    private void StopNotebook() => _notebookWindow?.Close();
}
