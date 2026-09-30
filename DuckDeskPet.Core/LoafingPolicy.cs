using System.Security.Cryptography;

namespace DuckDeskPet.Core;

public enum LoafingKind { None, NosePicking, WoodenFish }

/// <summary>Persistent break scheduling, independent of rendering and tick cadence</summary>
public static class LoafingPolicy
{
    public const decimal InitialWorkSeconds = 3600m;
    public const decimal RetrySeconds = 300m;
    public const decimal DurationSeconds = 1800m;
    public const int ChancePercent = 20;

    internal static void Normalize(PetState state)
    {
        if (state.LoafingRandomState == 0)
        {
            Span<byte> seed = stackalloc byte[8];
            RandomNumberGenerator.Fill(seed);
            state.LoafingRandomState = BitConverter.ToUInt64(seed);
            if (state.LoafingRandomState == 0) state.LoafingRandomState = 0x9E3779B97F4A7C15UL;
        }
        state.WorkUntilLoafingCheckSeconds = Math.Clamp(state.WorkUntilLoafingCheckSeconds, 0m, InitialWorkSeconds);
        state.LoafingRemainingSeconds = Math.Clamp(state.LoafingRemainingSeconds, 0m, DurationSeconds);
        if (!state.IsWorking || !Enum.IsDefined(state.Loafing) ||
            (state.Loafing != LoafingKind.None && state.LoafingRemainingSeconds == 0)) Reset(state);
        else if (state.Loafing == LoafingKind.None) state.LoafingRemainingSeconds = 0m;
    }

    internal static void Reset(PetState state)
    {
        state.Loafing = LoafingKind.None;
        state.LoafingRemainingSeconds = 0m;
        state.WorkUntilLoafingCheckSeconds = InitialWorkSeconds;
    }

    internal static ulong NextRandomState(ulong state)
    {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        return unchecked(state * 2685821657736338717UL);
    }

    internal static void Advance(PetState state, decimal seconds, Func<ulong, ulong> randomStep,
        Action<decimal, bool> accountSegment, out int changes)
    {
        changes = 0;
        if (!state.IsWorking) { accountSegment(seconds, false); return; }
        while (seconds > 0m || (!state.IsLoafing && state.WorkUntilLoafingCheckSeconds == 0m))
        {
            if (state.IsLoafing)
            {
                decimal part = Math.Min(seconds, state.LoafingRemainingSeconds);
                accountSegment(part, true);
                state.LoafingRemainingSeconds -= part;
                seconds -= part;
                if (state.LoafingRemainingSeconds == 0m) { Reset(state); changes++; }
            }
            else if (state.WorkUntilLoafingCheckSeconds == 0m)
            {
                state.LoafingRandomState = Draw(state.LoafingRandomState, randomStep);
                changes++;
                if (state.LoafingRandomState % 100UL < ChancePercent)
                {
                    state.LoafingRandomState = Draw(state.LoafingRandomState, randomStep);
                    state.Loafing = state.LoafingRandomState % 2UL == 0 ? LoafingKind.NosePicking : LoafingKind.WoodenFish;
                    state.LoafingRemainingSeconds = DurationSeconds;
                    state.WorkUntilLoafingCheckSeconds = InitialWorkSeconds;
                }
                else state.WorkUntilLoafingCheckSeconds = RetrySeconds;
            }
            else
            {
                decimal part = Math.Min(seconds, state.WorkUntilLoafingCheckSeconds);
                accountSegment(part, false);
                state.WorkUntilLoafingCheckSeconds -= part;
                seconds -= part;
            }
        }
    }

    internal static bool ExpireUncreditedTime(PetState state, decimal seconds)
    {
        if (!state.IsLoafing || seconds <= 0m) return false;
        state.LoafingRemainingSeconds = Math.Max(0m, state.LoafingRemainingSeconds - seconds);
        if (state.LoafingRemainingSeconds > 0m) return false;
        Reset(state);
        return true;
    }

    private static ulong Draw(ulong state, Func<ulong, ulong> randomStep)
    {
        ulong next = randomStep(state);
        if (next == 0) throw new InvalidOperationException("The loafing random step must return a nonzero persistent state");
        return next;
    }
}
