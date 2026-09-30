using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace DuckDeskPet;

// WebView2 1.0.4191.47 initializes its composition target in a private Loaded handler
// Delay its template hook until that handler finishes, so D3D creation has a local catch boundary
// Like the presenter, each instance belongs to one window and is disposed when that window closes
internal class GraphicsSafeWebView2CompositionControl : WebView2CompositionControl
{
    private readonly TaskCompletionSource _compositionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _hostLoaded, _applyingTemplate, _stopped;

    internal Task CompositionReady => _compositionReady.Task;
    internal event Action<COMException>? GraphicsInitializationFailed;

    internal GraphicsSafeWebView2CompositionControl() => Loaded += OnCompositionHostLoaded;

    private void OnCompositionHostLoaded(object sender, RoutedEventArgs e)
    {
        if (_stopped) return;
        _hostLoaded = true;
        Loaded -= OnCompositionHostLoaded;
        if (!ApplyTemplate()) OnApplyTemplate();
    }

    public override void OnApplyTemplate()
    {
        if (!_hostLoaded || _stopped || _applyingTemplate) return;
        _applyingTemplate = true;
        try
        {
            ApplyCompositionTemplate();
            _compositionReady.TrySetResult();
        }
        catch (COMException exception)
        {
            _stopped = true;
            _compositionReady.TrySetException(exception);
            GraphicsInitializationFailed?.Invoke(exception);
        }
        finally { _applyingTemplate = false; }
    }

    protected virtual void ApplyCompositionTemplate() => base.OnApplyTemplate();

    internal void StopGraphicsInitialization()
    {
        _stopped = true;
        Loaded -= OnCompositionHostLoaded;
        _compositionReady.TrySetCanceled();
    }
}
