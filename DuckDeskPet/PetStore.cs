using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DuckDeskPet.Core;

namespace DuckDeskPet;

internal static class AppPaths
{
    public static string DataDirectory => Path.GetFullPath(
        Environment.GetEnvironmentVariable("EAGLE_PET_DATA_DIR") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EagleDeskPet"));
}

internal sealed class PetStore
{
    private readonly string _directory;
    private readonly string _path;
    private readonly string _mutexName;
    private readonly object _gate = new();
    private string? _expectedFingerprint;
    private bool _observed;
    public string? Warning { get; private set; }
    public bool CanSave { get; private set; } = true;
    public bool MigrationWasApplied { get; private set; }
    public string? MigrationBackupPath { get; private set; }

    public PetStore(string? dataDirectory = null)
    {
        _directory = Path.GetFullPath(dataDirectory ?? AppPaths.DataDirectory);
        _path = Path.Combine(_directory, "pet-state.json");
        _mutexName = @"Local\EagleDeskPet.Save." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())));
    }

    public PetState? Load()
    {
        lock (_gate)
        {
            try
            {
                using var mutex = new Mutex(false, _mutexName);
                if (!Acquire(mutex)) throw new IOException("Save is in use.");
                try
                {
                    byte[]? bytes = ReadCurrentBytes();
                    PetState? state = bytes is null ? null : ReadState(bytes);
                    _expectedFingerprint = Fingerprint(bytes);
                    _observed = true;
                    CanSave = true;
                    Warning = null;
                    MigrationWasApplied = state is not null && state.Version < PetState.CurrentVersion;
                    MigrationBackupPath = null;
                    return state;
                }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                CanSave = false;
                Warning = "存档暂时读不了，原文件已保留。本次进度不会覆盖它。";
                return null;
            }
        }
    }

    public bool Save(PetState state)
    {
        lock (_gate)
        {
            if (!CanSave) return false;
            string? temporary = null;
            try
            {
                if (state.Version != PetState.CurrentVersion)
                    throw new InvalidDataException("Only current, migrated states can be saved.");
                EconomyPolicy.ValidateWalletLedger(state);
                if (state.Coins < 0 || state.Coins > EconomyPolicy.MaximumCoins || !EconomyPolicy.HasCentPrecision(state.Coins) ||
                    state.WageRemainderScaledUnits < 0 || state.WageRemainderScaledUnits >= 60m * EconomyPolicy.WageMoodScale ||
                    state.WorkExperienceRemainderUnits < 0 || state.WorkExperienceRemainderUnits >= 60m)
                    throw new InvalidDataException("Invalid precise economy state.");
                ContentOwnershipService.ValidateForSave(state.Content);
                if (!Enum.IsDefined(state.Loafing) || state.LoafingRemainingSeconds < 0m ||
                    state.LoafingRemainingSeconds > LoafingPolicy.DurationSeconds || state.WorkUntilLoafingCheckSeconds < 0m ||
                    state.WorkUntilLoafingCheckSeconds > LoafingPolicy.InitialWorkSeconds ||
                    (state.Loafing != LoafingKind.None && (!state.IsWorking || state.LoafingRemainingSeconds == 0m)) ||
                    (state.Loafing == LoafingKind.None && state.LoafingRemainingSeconds != 0m))
                    throw new InvalidDataException("Invalid loafing state");
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state,
                    new JsonSerializerOptions { WriteIndented = true });
                if (bytes.Length > 1_048_576) throw new InvalidDataException("Save too large.");
                using var mutex = new Mutex(false, _mutexName);
                if (!Acquire(mutex)) throw new IOException("Save is in use.");
                try
                {
                    byte[]? current = ReadCurrentBytes();
                    // Never overwrite a save this instance has not read, or one
                    // another process replaced after we read it. No silent merge.
                    if ((!_observed && current is not null) ||
                        (_observed && Fingerprint(current) != _expectedFingerprint))
                    {
                        CanSave = false;
                        Warning = "存档已被另一个实例修改，为避免覆盖，本实例已停止保存。请关闭多余实例后重新打开。";
                        return false;
                    }
                    Directory.CreateDirectory(_directory);
                    if (current is not null && ReadState(current).Version < PetState.CurrentVersion)
                    {
                        string legacyBackup = LegacyBackupPath(current);
                        PreserveLegacyBackup(legacyBackup, current);
                        MigrationBackupPath = legacyBackup;
                        MigrationWasApplied = true;
                    }
                    temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        stream.Write(bytes);
                        stream.Flush(flushToDisk: true);
                    }
                    if (current is not null) File.Replace(temporary, _path, _path + ".bak");
                    else File.Move(temporary, _path);
                    temporary = null;
                    _expectedFingerprint = Fingerprint(bytes);
                    _observed = true;
                    Warning = null;
                    return true;
                }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                Warning = "存档没有写成功，当前进度仍在内存里；鹰币交易不会确认扣款。";
                return false;
            }
            finally
            {
                // Only remove the uniquely named temp file owned by this attempt.
                if (temporary is not null)
                    try { File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Idempotent local debit. Failed persistence never changes the live wallet.</summary>
    public WalletTransactionResult TryDebit(PetState liveState, string transactionId, decimal amount) =>
        TryTransaction(liveState, transactionId, amount, _ => true);

    /// <summary>
    /// The future shop grants ownership on this detached candidate, so entitlement
    /// and debit are one durable write. Returning false cancels the whole operation.
    /// The callback must only mutate the candidate, never the live state or outside
    /// world; its wallet fields belong to this transaction coordinator.
    /// </summary>
    public WalletTransactionResult TryTransaction(PetState liveState, string transactionId, decimal amount,
        Func<PetState, bool> mutate)
    {
        lock (_gate)
        {
            WalletTransactionResult result;
            PetState? candidate;
            try { result = EconomyPolicy.PrepareDebit(liveState, transactionId, amount, out candidate); }
            catch (InvalidDataException)
            { return new(WalletTransactionStatus.InvalidRequest, "本地交易记录异常，未扣除鹰币。"); }
            if (!result.Changed) return result;
            decimal expectedCoins = candidate!.Coins;
            decimal expectedWageRemainder = candidate.WageRemainderScaledUnits;
            double expectedWageCursor = candidate.WageSettledWorkSeconds;
            DateTimeOffset expectedWageTime = candidate.WageLastUpdatedUtc;
            var expectedLoafing = (candidate.Loafing, candidate.LoafingRemainingSeconds,
                candidate.WorkUntilLoafingCheckSeconds, candidate.LoafingRandomState);
            var expectedLedger = new Dictionary<string, decimal>(candidate.AppliedWalletDebits, StringComparer.Ordinal);
            try
            {
                if (!mutate(candidate) || candidate.Version != PetState.CurrentVersion ||
                    candidate.Coins != expectedCoins || candidate.AppliedWalletDebits is null ||
                    candidate.WageRemainderScaledUnits != expectedWageRemainder ||
                    candidate.WageSettledWorkSeconds != expectedWageCursor || candidate.WageLastUpdatedUtc != expectedWageTime ||
                    (candidate.Loafing, candidate.LoafingRemainingSeconds, candidate.WorkUntilLoafingCheckSeconds,
                        candidate.LoafingRandomState) != expectedLoafing ||
                    candidate.AppliedWalletDebits.Count != expectedLedger.Count ||
                    expectedLedger.Any(x => !candidate.AppliedWalletDebits.TryGetValue(x.Key, out decimal value) || value != x.Value))
                    return new(WalletTransactionStatus.InvalidRequest, "交易条件不满足，未扣除鹰币。");
                // Detach any references the callback retained, and reject invalid
                // mutable collections before committing rather than after it.
                candidate = candidate.CreateSnapshot();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { return new(WalletTransactionStatus.InvalidRequest, "交易处理没有完成，未扣除鹰币。"); }
            if (!Save(candidate!)) return new(WalletTransactionStatus.SaveFailed, Warning ?? "鹰币交易未保存，未扣款。");
            liveState.ApplySnapshot(candidate);
            return result;
        }
    }

    private byte[]? ReadCurrentBytes()
    {
        if (!File.Exists(_path)) return null;
        if (new FileInfo(_path).Length > 1_048_576) throw new InvalidDataException("Save too large.");
        return File.ReadAllBytes(_path);
    }

    private static PetState ReadState(byte[] bytes)
    {
        // Keep the old ReadAllText BOM handling when loading legacy v1 files;
        // fingerprinting and backups still use the exact original bytes.
        using var text = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var state = JsonSerializer.Deserialize<PetState>(text.ReadToEnd()) ?? throw new InvalidDataException("Empty save.");
        if (state.Version is < 1 or > PetState.CurrentVersion)
            throw new InvalidDataException("Unsupported save version.");
        EconomyPolicy.ValidateWalletLedger(state);
        if (state.Version >= 2) ContentOwnershipService.ValidateForSave(state.Content);
        return state;
    }

    private string LegacyBackupPath(byte[] bytes) =>
        Path.Combine(_directory, "pet-state.pre-v4-" + Fingerprint(bytes)!.ToLowerInvariant() + ".json");

    private static void PreserveLegacyBackup(string path, byte[] bytes)
    {
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("The protected migration backup does not match the original save.");
            return;
        }
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static string? Fingerprint(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));

    private static bool Acquire(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(2)); }
        catch (AbandonedMutexException) { return true; }
    }
}

internal sealed class CompanionPreferences
{
    public bool NotificationsEnabled { get; set; } = true;
    public bool ActiveBanterEnabled { get; set; } = true;
    public bool AutoEmotionScenesEnabled { get; set; }
    public Dictionary<string, string> Applications { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static CompanionPreferences Load()
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDirectory, "companion.json");
            if (!File.Exists(path)) return new();
            var loaded = JsonSerializer.Deserialize<CompanionPreferences>(File.ReadAllText(path)) ?? new();
            loaded.Applications = new(loaded.Applications ?? new(), StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new();
        }
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            string path = Path.Combine(AppPaths.DataDirectory, "companion.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
