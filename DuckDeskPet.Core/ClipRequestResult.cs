namespace DuckDeskPet.Core;

public enum ClipRequestResult
{
    Started = 0,
    Queued = 1,
    ReplacedQueued = 2,
    IgnoredDuplicate = 3,
    RejectedPaused = 4,
}
