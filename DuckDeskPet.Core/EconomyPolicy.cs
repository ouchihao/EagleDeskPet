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
    public const int MaximumCoins = 999_999;
    public const double WageIntervalSeconds = 60.0;
    public const int CoinsPerWorkMinute = 1;
    // Never evict receipts: eviction would allow an old request to debit twice.
    public const int MaximumWalletTransactions = 4096;
    public const int MaximumTransactionIdLength = 80;

    /// <summary>
    /// Consume newly observed work once, crediting only the eligible portion.
    /// Time at the wallet cap is consumed too; it cannot be banked for later.
    /// </summary>
    public static int SettleWages(PetState state, double eligibleWorkSeconds)
    {
        if (!double.IsFinite(eligibleWorkSeconds) || eligibleWorkSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(eligibleWorkSeconds));
        if (state.Coins < 0 || state.Coins > MaximumCoins ||
            !double.IsFinite(state.TotalWorkSeconds) || state.TotalWorkSeconds < 0 ||
            !double.IsFinite(state.WageSettledWorkSeconds) || state.WageSettledWorkSeconds < 0 ||
            !double.IsFinite(state.WageProgressSeconds) || state.WageProgressSeconds < 0 || state.WageProgressSeconds >= WageIntervalSeconds)
            throw new ArgumentException("Wages require a normalized state.", nameof(state));
        double newlyObserved = Math.Max(0.0, state.TotalWorkSeconds - state.WageSettledWorkSeconds);
        state.WageSettledWorkSeconds = Math.Max(state.WageSettledWorkSeconds, state.TotalWorkSeconds);
        double accumulated = state.WageProgressSeconds + Math.Min(newlyObserved, eligibleWorkSeconds);
        double earned = Math.Floor((accumulated + 1e-7) / WageIntervalSeconds);
        state.WageProgressSeconds = Math.Clamp(accumulated - earned * WageIntervalSeconds, 0, WageIntervalSeconds);
        int credited = (int)Math.Min(earned * CoinsPerWorkMinute, MaximumCoins - state.Coins);
        state.Coins += credited;
        return credited;
    }

    /// <summary>Prepare a detached debit; the caller must durably save before exposing it.</summary>
    public static WalletTransactionResult PrepareDebit(PetState state, string transactionId, int amount,
        out PetState? candidate)
    {
        candidate = null;
        if (!IsValidTransactionId(transactionId) || amount <= 0 || amount > MaximumCoins ||
            state.Version != PetState.CurrentVersion || state.Coins < 0 || state.Coins > MaximumCoins)
            return new(WalletTransactionStatus.InvalidRequest, "鹰币交易参数不正确。");
        ValidateWalletLedger(state);
        if (state.AppliedWalletDebits.TryGetValue(transactionId, out int previousAmount))
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
        return new(WalletTransactionStatus.Applied, $"已扣除 {amount} 鹰币。");
    }

    public static void ValidateWalletLedger(PetState state)
    {
        if (state.Version == 1) return; // v1 has no wallet; migration ignores added fields.
        if (state.AppliedWalletDebits is null || state.AppliedWalletDebits.Count > MaximumWalletTransactions ||
            state.AppliedWalletDebits.Any(x => !IsValidTransactionId(x.Key) || x.Value <= 0 || x.Value > MaximumCoins))
            throw new InvalidDataException("Invalid wallet transaction ledger.");
    }

    private static bool IsValidTransactionId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= MaximumTransactionIdLength &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}
