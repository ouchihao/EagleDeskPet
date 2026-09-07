using System;

namespace DuckDeskPet.Core;

/// <summary>
/// Deterministic whole-body motion with interruption-safe anticipation,
/// transition, and settle phases.
/// </summary>
public sealed class PetAnimator
{
    public const double MaximumDeltaSeconds = 0.050;
    public const double MinimumScale = 0.84;
    public const double MaximumScale = 1.16;
    public const double MaximumAbsoluteRotationDegrees = 12.0;
    public const double MaximumAbsoluteOffsetXDip = 12.0;
    public const double MinimumOffsetYDip = -34.0;
    public const double MaximumOffsetYDip = 10.0;
    public const double AnticipationDurationSeconds = 0.16;
    public const double SettleDurationSeconds = 0.24;
    public const double ClickReactionDurationSeconds = 1.02;

    private const double Tau = Math.PI * 2.0;
    private const double MinimumIdleDelaySeconds = 2.0;
    private const double MaximumIdleDelaySeconds = 5.0;

    private uint _randomState;
    private double _motionTimeSeconds;
    private double _stateElapsedSeconds;
    private double _secondsUntilAction;
    private AnimationAction _lastAction;
    private Pose _transitionStartPose;

    public PetAnimator(uint randomSeed = 0xD0C0_2026u)
    {
        _randomState = randomSeed == 0 ? 0xA341_316Cu : randomSeed;
        State = AnimationState.Idle;
        CurrentAction = AnimationAction.None;
        CurrentPose = Pose.Neutral;
        _transitionStartPose = Pose.Neutral;
        ScheduleNextAction();
    }

    public AnimationState State { get; private set; }

    public AnimationAction CurrentAction { get; private set; }

    public Pose CurrentPose { get; private set; }

    public double StateElapsedSeconds => _stateElapsedSeconds;

    public bool IsPaused => State == AnimationState.Paused;

    public AnimationPhase CurrentPhase
    {
        get
        {
            if (State == AnimationState.Paused)
            {
                return AnimationPhase.Paused;
            }

            if (State == AnimationState.Idle)
            {
                return AnimationPhase.Idle;
            }

            double totalDuration = State == AnimationState.ClickReaction
                ? ClickReactionDurationSeconds
                : GetActionDurationSeconds(CurrentAction);
            if (_stateElapsedSeconds < AnticipationDurationSeconds)
            {
                return AnimationPhase.Anticipation;
            }

            return _stateElapsedSeconds < totalDuration - SettleDurationSeconds
                ? AnimationPhase.Transition
                : AnimationPhase.Settle;
        }
    }

    public Pose Advance(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deltaSeconds),
                "Delta time must be finite and non-negative.");
        }

        if (State == AnimationState.Paused)
        {
            CurrentPose = Pose.Neutral;
            return CurrentPose;
        }

        double boundedDeltaSeconds = Math.Min(deltaSeconds, MaximumDeltaSeconds);
        if (boundedDeltaSeconds > 0.0)
        {
            _motionTimeSeconds += boundedDeltaSeconds;
            AdvanceState(boundedDeltaSeconds);
        }

        CurrentPose = Constrain(SamplePose());
        return CurrentPose;
    }

    public bool TriggerClick()
    {
        if (IsPaused)
        {
            return false;
        }

        _transitionStartPose = CurrentPose;
        State = AnimationState.ClickReaction;
        CurrentAction = AnimationAction.None;
        _stateElapsedSeconds = 0.0;
        CurrentPose = Constrain(SamplePose());
        return true;
    }

    public bool TriggerShy() => TriggerAction(AnimationAction.Shy);

    /// <summary>
    /// Maps an expression cue to matching whole-body choreography.
    /// </summary>
    public bool TriggerExpression(ExpressionKind expression) => expression switch
    {
        ExpressionKind.SideEye => TriggerAction(AnimationAction.Wiggle),
        ExpressionKind.Bomb => TriggerAction(AnimationAction.Shimmy),
        ExpressionKind.Yawn => TriggerAction(AnimationAction.Stretch),
        ExpressionKind.Shy => TriggerAction(AnimationAction.Shy),
        _ => throw new ArgumentOutOfRangeException(
            nameof(expression),
            "Idle does not map to a triggered body action."),
    };

    public bool TriggerAction(AnimationAction action)
    {
        uint actionValue = (uint)action;
        if (actionValue < (uint)AnimationAction.Wiggle ||
            actionValue > (uint)AnimationAction.Shy)
        {
            throw new ArgumentOutOfRangeException(nameof(action), "The animation action is not recognized.");
        }

        if (IsPaused)
        {
            return false;
        }

        BeginAction(action, CurrentPose);
        CurrentPose = Constrain(SamplePose());
        return true;
    }

    public void SetPaused(bool paused)
    {
        if (paused)
        {
            State = AnimationState.Paused;
            CurrentAction = AnimationAction.None;
            _stateElapsedSeconds = 0.0;
            _transitionStartPose = Pose.Neutral;
            CurrentPose = Pose.Neutral;
            return;
        }

        if (State == AnimationState.Paused)
        {
            BeginIdle();
            CurrentPose = Constrain(SampleIdlePose());
        }
    }

    public static double GetActionDurationSeconds(AnimationAction action) => action switch
    {
        AnimationAction.Wiggle => 0.82,
        AnimationAction.Hop => 1.05,
        AnimationAction.Nod => 0.72,
        AnimationAction.Shimmy => 1.10,
        AnimationAction.Stretch => 1.12,
        AnimationAction.Shy => 1.35,
        _ => throw new ArgumentOutOfRangeException(nameof(action), "The action has no duration."),
    };

    private void AdvanceState(double deltaSeconds)
    {
        double remainingSeconds = deltaSeconds;

        for (int transitionCount = 0; remainingSeconds > 0.0 && transitionCount < 4; transitionCount++)
        {
            switch (State)
            {
                case AnimationState.Idle:
                    if (remainingSeconds < _secondsUntilAction)
                    {
                        _secondsUntilAction -= remainingSeconds;
                        remainingSeconds = 0.0;
                    }
                    else
                    {
                        remainingSeconds -= _secondsUntilAction;
                        BeginAction(ChooseNextAction(), CurrentPose);
                    }

                    break;

                case AnimationState.PerformingAction:
                {
                    double timeToBoundary =
                        GetActionDurationSeconds(CurrentAction) - _stateElapsedSeconds;
                    if (remainingSeconds < timeToBoundary)
                    {
                        _stateElapsedSeconds += remainingSeconds;
                        remainingSeconds = 0.0;
                    }
                    else
                    {
                        remainingSeconds -= Math.Max(0.0, timeToBoundary);
                        BeginIdle();
                    }

                    break;
                }

                case AnimationState.ClickReaction:
                {
                    double timeToBoundary = ClickReactionDurationSeconds - _stateElapsedSeconds;
                    if (remainingSeconds < timeToBoundary)
                    {
                        _stateElapsedSeconds += remainingSeconds;
                        remainingSeconds = 0.0;
                    }
                    else
                    {
                        remainingSeconds -= Math.Max(0.0, timeToBoundary);
                        BeginIdle();
                    }

                    break;
                }

                case AnimationState.Paused:
                    remainingSeconds = 0.0;
                    break;

                default:
                    throw new InvalidOperationException($"Unknown animation state: {State}.");
            }
        }
    }

    private Pose SamplePose()
    {
        Pose idle = SampleIdlePose();
        if (State == AnimationState.Idle)
        {
            return idle;
        }

        if (State == AnimationState.Paused)
        {
            return Pose.Neutral;
        }

        bool isClick = State == AnimationState.ClickReaction;
        AnimationAction action = isClick ? AnimationAction.Hop : CurrentAction;
        double totalDuration = isClick
            ? ClickReactionDurationSeconds
            : GetActionDurationSeconds(action);
        double transitionDuration =
            totalDuration - AnticipationDurationSeconds - SettleDurationSeconds;

        if (_stateElapsedSeconds < AnticipationDurationSeconds)
        {
            Pose target = Pose.Compose(idle, GetAnticipationPose(action, isClick));
            return Pose.Lerp(
                _transitionStartPose,
                target,
                SmootherStep(_stateElapsedSeconds / AnticipationDurationSeconds));
        }

        if (_stateElapsedSeconds < AnticipationDurationSeconds + transitionDuration)
        {
            double progress =
                (_stateElapsedSeconds - AnticipationDurationSeconds) / transitionDuration;
            return Pose.Compose(idle, SampleTransitionPose(action, isClick, progress));
        }

        double settleProgress =
            (_stateElapsedSeconds - AnticipationDurationSeconds - transitionDuration) /
            SettleDurationSeconds;
        return Pose.Compose(idle, SampleSettlePose(action, isClick, settleProgress));
    }

    private Pose SampleIdlePose()
    {
        double breath = Math.Sin(_motionTimeSeconds * Tau / 3.2);
        double sway = Math.Sin(_motionTimeSeconds * Tau / 5.1);

        return new Pose(
            1.0 - (0.006 * breath),
            1.0 + (0.012 * breath),
            0.45 * sway,
            0.0,
            -1.25 * breath);
    }

    private static Pose SampleTransitionPose(
        AnimationAction action,
        bool isClick,
        double progress)
    {
        double bounded = Math.Clamp(progress, 0.0, 1.0);
        double amount = SmootherStep(bounded);
        double envelope = Math.Sin(Math.PI * bounded);
        Pose pose = Pose.Lerp(
            GetAnticipationPose(action, isClick),
            GetSettleEntryPose(action, isClick),
            amount);

        if (isClick)
        {
            double arc = 4.0 * bounded * (1.0 - bounded);
            return AddToPose(
                pose,
                -0.035 * envelope,
                0.060 * envelope,
                3.2 * Math.Sin(Tau * bounded) * envelope,
                0.0,
                -24.0 * arc);
        }

        return action switch
        {
            AnimationAction.Wiggle => AddToPose(
                pose,
                0.0,
                0.0,
                5.5 * Math.Sin(4.0 * Math.PI * bounded) * envelope,
                2.4 * Math.Sin(4.0 * Math.PI * bounded) * envelope,
                0.0),
            AnimationAction.Hop => AddToPose(
                pose,
                -0.040 * envelope,
                0.060 * envelope,
                2.0 * Math.Sin(Tau * bounded) * envelope,
                0.0,
                -22.0 * 4.0 * bounded * (1.0 - bounded)),
            AnimationAction.Nod => AddToPose(
                pose,
                0.025 * envelope,
                -0.035 * envelope,
                4.0 * Math.Sin(Tau * bounded) * envelope,
                0.0,
                2.0 * envelope),
            AnimationAction.Shimmy => AddToPose(
                pose,
                0.025 * envelope,
                -0.035 * envelope,
                7.0 * Math.Sin(6.0 * Math.PI * bounded) * envelope,
                4.0 * Math.Sin(4.0 * Math.PI * bounded) * envelope,
                1.5 * envelope),
            AnimationAction.Stretch => AddToPose(
                pose,
                -0.070 * envelope,
                0.080 * envelope,
                1.5 * Math.Sin(Tau * bounded) * envelope,
                0.0,
                -6.0 * envelope),
            AnimationAction.Shy => AddToPose(
                pose,
                0.030 * envelope,
                -0.045 * envelope,
                5.0 * Math.Sin(4.0 * Math.PI * bounded) * envelope,
                3.0 * Math.Sin(Tau * bounded) * envelope,
                2.0 * envelope),
            _ => Pose.Neutral,
        };
    }

    private static Pose SampleSettlePose(
        AnimationAction action,
        bool isClick,
        double progress)
    {
        double bounded = Math.Clamp(progress, 0.0, 1.0);
        Pose pose = Pose.Lerp(
            GetSettleEntryPose(action, isClick),
            Pose.Neutral,
            SmootherStep(bounded));
        double envelope = Math.Sin(Math.PI * bounded) * (1.0 - bounded);
        double amplitude = isClick ? 3.0 : action switch
        {
            AnimationAction.Shimmy => 3.5,
            AnimationAction.Shy => -2.8,
            AnimationAction.Wiggle => 2.5,
            _ => 1.5,
        };

        return AddToPose(
            pose,
            0.018 * envelope,
            -0.014 * envelope,
            amplitude * Math.Sin(4.0 * Math.PI * bounded) * envelope,
            0.8 * Math.Sin(2.0 * Math.PI * bounded) * envelope,
            1.2 * envelope);
    }

    private static Pose GetAnticipationPose(AnimationAction action, bool isClick)
    {
        if (isClick)
        {
            return new Pose(1.10, 0.86, 0.0, 0.0, 5.0);
        }

        return action switch
        {
            AnimationAction.Wiggle => new Pose(0.98, 0.98, -2.0, -1.0, 1.0),
            AnimationAction.Hop => new Pose(1.08, 0.89, 0.0, 0.0, 4.0),
            AnimationAction.Nod => new Pose(1.03, 0.94, -1.0, 0.0, 2.0),
            AnimationAction.Shimmy => new Pose(1.08, 0.90, -3.0, -2.0, 3.0),
            AnimationAction.Stretch => new Pose(1.08, 0.89, 0.0, 0.0, 4.0),
            AnimationAction.Shy => new Pose(1.07, 0.89, -5.0, -3.0, 4.0),
            _ => Pose.Neutral,
        };
    }

    private static Pose GetSettleEntryPose(AnimationAction action, bool isClick)
    {
        if (isClick)
        {
            return new Pose(1.08, 0.92, 2.0, 0.0, 3.0);
        }

        return action switch
        {
            AnimationAction.Wiggle => new Pose(1.01, 0.99, 1.0, 0.5, 0.5),
            AnimationAction.Hop => new Pose(1.07, 0.92, 0.0, 0.0, 3.0),
            AnimationAction.Nod => new Pose(1.02, 0.96, 1.0, 0.0, 1.0),
            AnimationAction.Shimmy => new Pose(1.06, 0.93, 3.0, 2.0, 2.0),
            AnimationAction.Stretch => new Pose(0.98, 1.05, 0.0, 0.0, -2.0),
            AnimationAction.Shy => new Pose(1.05, 0.93, 4.0, 2.0, 2.0),
            _ => Pose.Neutral,
        };
    }

    private void BeginIdle()
    {
        State = AnimationState.Idle;
        CurrentAction = AnimationAction.None;
        _stateElapsedSeconds = 0.0;
        _transitionStartPose = Pose.Neutral;
        ScheduleNextAction();
    }

    private void BeginAction(AnimationAction action, Pose startingPose)
    {
        State = AnimationState.PerformingAction;
        CurrentAction = action;
        _lastAction = action;
        _transitionStartPose = startingPose.IsFinite ? startingPose : Pose.Neutral;
        _stateElapsedSeconds = 0.0;
    }

    private AnimationAction ChooseNextAction()
    {
        const int firstActionValue = (int)AnimationAction.Wiggle;
        const int actionCount = 6;

        int previousValue = (int)_lastAction;
        if (_lastAction == AnimationAction.None)
        {
            return (AnimationAction)(firstActionValue + (NextUInt32() % actionCount));
        }

        int candidate = firstActionValue + (int)(NextUInt32() % (actionCount - 1));
        if (candidate >= previousValue)
        {
            candidate++;
        }

        return (AnimationAction)candidate;
    }

    private void ScheduleNextAction()
    {
        _secondsUntilAction = MinimumIdleDelaySeconds +
            ((MaximumIdleDelaySeconds - MinimumIdleDelaySeconds) * NextUnitDouble());
    }

    private uint NextUInt32()
    {
        uint value = _randomState;
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        _randomState = value;
        return value;
    }

    private double NextUnitDouble() =>
        (NextUInt32() >> 8) * (1.0 / 16_777_216.0);

    private static Pose AddToPose(
        Pose pose,
        double scaleX,
        double scaleY,
        double rotation,
        double offsetX,
        double offsetY) => new(
            pose.ScaleX + scaleX,
            pose.ScaleY + scaleY,
            pose.RotationDegrees + rotation,
            pose.OffsetXDip + offsetX,
            pose.OffsetYDip + offsetY);

    private static double SmootherStep(double progress)
    {
        double bounded = Math.Clamp(progress, 0.0, 1.0);
        return bounded * bounded * bounded *
            ((bounded * ((bounded * 6.0) - 15.0)) + 10.0);
    }

    private static Pose Constrain(Pose pose)
    {
        if (!pose.IsFinite)
        {
            return Pose.Neutral;
        }

        return new Pose(
            Math.Clamp(pose.ScaleX, MinimumScale, MaximumScale),
            Math.Clamp(pose.ScaleY, MinimumScale, MaximumScale),
            Math.Clamp(
                pose.RotationDegrees,
                -MaximumAbsoluteRotationDegrees,
                MaximumAbsoluteRotationDegrees),
            Math.Clamp(
                pose.OffsetXDip,
                -MaximumAbsoluteOffsetXDip,
                MaximumAbsoluteOffsetXDip),
            Math.Clamp(pose.OffsetYDip, MinimumOffsetYDip, MaximumOffsetYDip));
    }
}
