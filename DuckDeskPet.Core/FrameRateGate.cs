using System;

namespace DuckDeskPet.Core;

/// <summary>
/// Reduces compositor callbacks to a requested long-term update rate without
/// sleeping, queueing work, or emitting catch-up frames after a stall. On a
/// display whose refresh rate is not divisible by the target, accepted display
/// intervals are necessarily uneven while their long-term average is stable.
/// </summary>
public sealed class FrameRateGate
{
    public const double DefaultTargetFramesPerSecond = 60.0;
    public const double DefaultMaximumDeltaSeconds = 0.050;
    public const double DefaultEarlyToleranceSeconds = 0.0005;
    public const double DefaultStallResetSeconds = 0.250;

    private readonly double _frameIntervalSeconds;
    private readonly double _maximumDeltaSeconds;
    private readonly double _earlyToleranceSeconds;
    private readonly double _stallResetSeconds;

    private bool _isInitialized;
    private double _lastObservedSeconds;
    private double _lastAcceptedSeconds;
    private double _nextDueSeconds;

    public FrameRateGate(
        double targetFramesPerSecond = DefaultTargetFramesPerSecond,
        double maximumDeltaSeconds = DefaultMaximumDeltaSeconds,
        double earlyToleranceSeconds = DefaultEarlyToleranceSeconds,
        double stallResetSeconds = DefaultStallResetSeconds)
    {
        if (!double.IsFinite(targetFramesPerSecond) || targetFramesPerSecond <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetFramesPerSecond),
                "The target frame rate must be finite and greater than zero.");
        }

        if (!double.IsFinite(maximumDeltaSeconds) || maximumDeltaSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDeltaSeconds),
                "The maximum delta must be finite and greater than zero.");
        }

        if (!double.IsFinite(earlyToleranceSeconds) || earlyToleranceSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(earlyToleranceSeconds),
                "The early tolerance must be finite and non-negative.");
        }

        if (!double.IsFinite(stallResetSeconds) || stallResetSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stallResetSeconds),
                "The stall reset interval must be finite and greater than zero.");
        }

        TargetFramesPerSecond = targetFramesPerSecond;
        _frameIntervalSeconds = 1.0 / targetFramesPerSecond;
        _maximumDeltaSeconds = maximumDeltaSeconds;
        _earlyToleranceSeconds = Math.Min(earlyToleranceSeconds, _frameIntervalSeconds * 0.25);
        _stallResetSeconds = stallResetSeconds;
    }

    public double TargetFramesPerSecond { get; }

    public long AcceptedFrameCount { get; private set; }

    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// Returns true when the caller should update and apply a pose. The input
    /// is expected to be RenderingEventArgs.RenderingTime.TotalSeconds.
    /// </summary>
    public bool TryAdvance(double compositorTimeSeconds, out double deltaSeconds)
    {
        if (!double.IsFinite(compositorTimeSeconds) || compositorTimeSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(compositorTimeSeconds),
                "Compositor time must be finite and non-negative.");
        }

        deltaSeconds = 0.0;

        if (!_isInitialized)
        {
            InitializeAt(compositorTimeSeconds);
            AcceptedFrameCount++;
            return true;
        }

        if (compositorTimeSeconds == _lastObservedSeconds)
        {
            return false;
        }

        if (compositorTimeSeconds < _lastObservedSeconds)
        {
            // RenderingTime can restart after a render-target or session reset.
            InitializeAt(compositorTimeSeconds);
            AcceptedFrameCount++;
            return true;
        }

        _lastObservedSeconds = compositorTimeSeconds;

        if (compositorTimeSeconds + _earlyToleranceSeconds < _nextDueSeconds)
        {
            return false;
        }

        double rawDeltaSeconds = compositorTimeSeconds - _lastAcceptedSeconds;
        _lastAcceptedSeconds = compositorTimeSeconds;
        deltaSeconds = Math.Min(rawDeltaSeconds, _maximumDeltaSeconds);

        if (rawDeltaSeconds >= _stallResetSeconds)
        {
            _nextDueSeconds = compositorTimeSeconds + _frameIntervalSeconds;
        }
        else
        {
            // Preserve the 60-Hz phase on ordinary displays. This loop only
            // advances a few slots (normally one or two); stalls reset above.
            do
            {
                _nextDueSeconds += _frameIntervalSeconds;
            }
            while (_nextDueSeconds <= compositorTimeSeconds + _earlyToleranceSeconds);
        }

        AcceptedFrameCount++;
        return true;
    }

    public void Reset()
    {
        _isInitialized = false;
        _lastObservedSeconds = 0.0;
        _lastAcceptedSeconds = 0.0;
        _nextDueSeconds = 0.0;
        AcceptedFrameCount = 0;
    }

    private void InitializeAt(double compositorTimeSeconds)
    {
        _isInitialized = true;
        _lastObservedSeconds = compositorTimeSeconds;
        _lastAcceptedSeconds = compositorTimeSeconds;
        _nextDueSeconds = compositorTimeSeconds + _frameIntervalSeconds;
    }
}
