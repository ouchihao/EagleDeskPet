namespace DuckDeskPet.Core;

public sealed class QuickAccessPolicy
{
    public const double RevealDelaySeconds = .12;
    public const double LeaveDelaySeconds = .42;
    private double? _enteredAt;
    private double? _leftAt;
    private bool _dismissed;
    public bool IsVisible { get; private set; }

    public bool Update(double seconds, bool nearPet, bool nearBar, bool blocked, bool barStillVisible = false)
    {
        if (blocked)
        {
            Reset();
            return false;
        }
        if (_dismissed)
        {
            if (!nearPet && !nearBar) _dismissed = false;
            return false;
        }
        if (nearPet || (nearBar && (IsVisible || barStillVisible)))
        {
            _leftAt = null;
            _enteredAt ??= seconds;
            if ((nearBar && barStillVisible) || seconds - _enteredAt >= RevealDelaySeconds) IsVisible = true;
        }
        else
        {
            _enteredAt = null;
            _leftAt ??= seconds;
            if (seconds - _leftAt >= LeaveDelaySeconds) IsVisible = false;
        }
        return IsVisible;
    }

    public void Reset(bool dismissUntilLeave = false)
    {
        IsVisible = false;
        _enteredAt = _leftAt = null;
        _dismissed = dismissUntilLeave;
    }
}
