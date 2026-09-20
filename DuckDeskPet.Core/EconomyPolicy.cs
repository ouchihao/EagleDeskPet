using System.IO;

namespace DuckDeskPet.Core;

public enum WalletTransactionStatus
{
    Applied,
    AlreadyApplied,
    InvalidRequest,
    InsufficientFunds,
    LedgerFull,
    SaveFailed,
}

public readonly record struct WalletTransactionResult(WalletTransactionStatus Status, string Message)
{
    public bool Success => Status is WalletTransactionStatus.Applied or WalletTransactionStatus.AlreadyApplied;
    public bool Changed => Status == WalletTransactionStatus.Applied;
}

/// <summary>Local demo currency only. No payments, accounts, or exchange value.</summary>
public static class EconomyPolicy
{
    public const decimal MaximumCoins = 999_999m;
    public const double WageIntervalSeconds = 60.0;
    public const int CoinsPerWorkMinute = 1;
    // Never evict receipts: eviction would allow an old request to debit twice.
    public const int MaximumWalletTransactions = 4096;
    public const int MaximumTransactionIdLength = 80;

    /// <summary>
    /// Consume newly observed work once, crediting only the eligible portion.
    /// Time at the wallet cap is consumed too; it cannot be banked for later.
    /// </summary>
    public static decimal SettleWages(PetState state, double eligibleWorkSeconds, decimal coinMultiplier = 1m)
    {
        if (!double.IsFinite(eligibleWorkSeconds) || eligibleWorkSeconds < 0 ||
            coinMultiplier < 1m || coinMultiplier > 1m + EquipmentPolicy.MaximumCoinBonus)
            throw new ArgumentOutOfRangeException(nameof(eligibleWorkSeconds));
        if (state.Coins < 0 || state.Coins > MaximumCoins || !HasCentPrecision(state.Coins) ||
            !double.IsFinite(state.TotalWorkSeconds) || state.TotalWorkSeconds < 0 ||
            !double.IsFinite(state.WageSettledWorkSeconds) || state.WageSettledWorkSeconds < 0 ||
            state.WageRemainderUnits < 0 || state.WageRemainderUnits >= 60m)
            throw new ArgumentException("Wages require a normalized state.", nameof(state));
        double newlyObserved = Math.Max(0.0, state.TotalWorkSeconds - state.WageSettledWorkSeconds);
        state.WageSettledWorkSeconds = Math.Max(state.WageSettledWorkSeconds, state.TotalWorkSeconds);
        decimal seconds = ExactSeconds(Math.Min(newlyObserved, eligibleWorkSeconds));
        if (seconds <= 0m) return 0m;
        // One cent costs 0.6 base coin-seconds. Both operands remain terminating
        // decimals, so splitting Advance or serializing between ticks is neutral.
        decimal accumulated = state.WageRemainderUnits + seconds * coinMultiplier;
        decimal earned = decimal.Floor(accumulated / 0.6m) / 100m;
        state.WageRemainderUnits = accumulated - earned * 60m;
        decimal credited = Math.Min(earned, MaximumCoins - state.Coins);
        state.Coins += credited;
        if (state.Coins == MaximumCoins) state.WageRemainderUnits = 0m;
        state.WageProgressSeconds = (double)state.WageRemainderUnits;
        return credited;
    }

    /// <summary>Prepare a detached debit; the caller must durably save before exposing it.</summary>
    public static WalletTransactionResult PrepareDebit(PetState state, string transactionId, decimal amount,
        out PetState? candidate)
    {
        candidate = null;
        if (!IsValidTransactionId(transactionId) || amount <= 0 || amount > MaximumCoins || !HasCentPrecision(amount) ||
            state.Version != PetState.CurrentVersion || state.Coins < 0 || state.Coins > MaximumCoins || !HasCentPrecision(state.Coins))
            return new(WalletTransactionStatus.InvalidRequest, "鹰币交易参数不正确。");
        ValidateWalletLedger(state);
        if (state.AppliedWalletDebits.TryGetValue(transactionId, out decimal previousAmount))
            return previousAmount == amount
                ? new(WalletTransactionStatus.AlreadyApplied, "这笔鹰币已经结算过了，没有重复扣除。")
                : new(WalletTransactionStatus.InvalidRequest, "交易编号已用于另一笔金额。");
        if (state.AppliedWalletDebits.Count >= MaximumWalletTransactions)
            return new(WalletTransactionStatus.LedgerFull, "本地交易记录已满，暂时不能进行新交易。");
        if (state.Coins < amount)
            return new(WalletTransactionStatus.InsufficientFunds, "鹰币还不够，再工作一会儿吧。");
        candidate = state.CreateSnapshot();
        candidate.Coins -= amount;
        candidate.AppliedWalletDebits.Add(transactionId, amount);
        return new(WalletTransactionStatus.Applied, $"已扣除 {amount:F2} 鹰币。");
    }

    public static void ValidateWalletLedger(PetState state)
    {
        if (state.Version == 1) return; // v1 has no wallet; migration ignores added fields.
        if (state.AppliedWalletDebits is null || state.AppliedWalletDebits.Count > MaximumWalletTransactions ||
            state.AppliedWalletDebits.Any(x => !IsValidTransactionId(x.Key) || x.Value <= 0 || x.Value > MaximumCoins || !HasCentPrecision(x.Value)))
            throw new InvalidDataException("Invalid wallet transaction ledger.");
    }

    public static bool HasCentPrecision(decimal value) => decimal.Round(value, 2) == value;

    internal static decimal ExactSeconds(double seconds) => (decimal)Math.Round(seconds, 7, MidpointRounding.ToEven);

    private static bool IsValidTransactionId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= MaximumTransactionIdLength &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}
