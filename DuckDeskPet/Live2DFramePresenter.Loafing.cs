namespace DuckDeskPet;

internal sealed partial class Live2DFramePresenter
{
    private long _loafingRevision;
    private string? _lastLoafingPhase, _lastLoafingKind;
    private bool _lastLoafingPaused;
    private double _lastLoafingSentAt = double.NegativeInfinity;

    internal void SendLoafing(string phase, double seconds, bool paused, string kind)
    {
        if (!IsActive || IsPreparing || _disposed || _failed) return;
        if (phase is not ("none" or "throw" or "loop" or "return") ||
            kind is not ("None" or "NosePicking" or "WoodenFish") ||
            (phase != "none" && kind == "None") || !double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentException("Invalid loafing presentation sample");
        double now = _clock.Elapsed.TotalSeconds;
        bool changed = phase != _lastLoafingPhase || kind != _lastLoafingKind || paused != _lastLoafingPaused;
        if (!changed && (phase == "none" || now - _lastLoafingSentAt < .05)) return;
        try
        {
            Post(new { type = "loafing", protocol = 1, generation = _acknowledgements.Generation,
                revision = ++_loafingRevision, phase, seconds, paused, kind });
            _lastLoafingPhase = phase;
            _lastLoafingKind = kind;
            _lastLoafingPaused = paused;
            _lastLoafingSentAt = now;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(ex.Message); }
    }
}
