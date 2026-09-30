using DuckDeskPet.Core;

namespace DuckDeskPet;

internal sealed class PresentationAcknowledgementGate(long generation)
{
    private ClipSample _sent = ClipSample.Idle;
    private long _acknowledgedSequence = -1;
    private ClipKind _acknowledgedKind;
    private bool _hasSent;

    internal long Generation { get; } = generation;

    internal void Sent(ClipSample sample)
    {
        _sent = sample;
        _hasSent = true;
    }

    internal bool Acknowledge(long generation, long sequence, ClipKind kind)
    {
        if (generation != Generation || !_hasSent || _sent.Phase != ClipPlaybackPhase.Terminal ||
            _sent.Sequence != sequence || _sent.Kind != kind ||
            (_acknowledgedSequence == sequence && _acknowledgedKind == kind))
            return false;
        _acknowledgedSequence = sequence;
        _acknowledgedKind = kind;
        return true;
    }

    internal bool CanLeave(ClipSample sample) => sample.Phase != ClipPlaybackPhase.Terminal ||
        (_acknowledgedSequence == sample.Sequence && _acknowledgedKind == sample.Kind);
}
