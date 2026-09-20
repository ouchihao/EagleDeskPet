using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DuckDeskPet.Core;

namespace DuckDeskPet;

/// <summary>Separate, local-only reminder file. Never reads pet saves or AI credentials.</summary>
internal sealed class ReminderStore
{
    private readonly string _directory;
    private readonly string _path;
    private readonly string _mutexName;
    private string? _expectedFingerprint;
    private bool _observed;
    private bool _readOnly;
    internal string? Warning { get; private set; }

    internal ReminderStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        _path = Path.Combine(_directory, "reminders.json");
        _mutexName = "EagleDeskPet.Reminders." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())))[..24];
    }

    internal ReminderSnapshot Load()
    {
        using var mutex = new Mutex(false, _mutexName);
        bool acquired = false;
        try
        {
            acquired = Acquire(mutex);
            if (!acquired) throw new IOException("Reminder state is busy.");
            byte[]? bytes = ReadBytes();
            var state = bytes is null ? new ReminderSnapshot() : JsonSerializer.Deserialize<ReminderSnapshot>(bytes)
                ?? throw new InvalidDataException("Empty reminder state.");
            ReminderScheduler.Validate(state);
            _expectedFingerprint = Fingerprint(bytes);
            _observed = true;
            Warning = null;
            return state;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            _readOnly = true;
            Warning = "提醒文件无法读取，已保留原文件并停止写入；请关闭其他实例后检查 reminders.json。";
            return new();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    internal bool Save(ReminderSnapshot state)
    {
        if (_readOnly) return false;
        string? temporary = null;
        using var mutex = new Mutex(false, _mutexName);
        bool acquired = false;
        try
        {
            ReminderScheduler.Validate(state);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, new JsonSerializerOptions { WriteIndented = true });
            acquired = Acquire(mutex);
            if (!acquired) throw new IOException("Reminder state is busy.");
            byte[]? current = ReadBytes();
            if ((!_observed && current is not null) || (_observed && Fingerprint(current) != _expectedFingerprint))
            {
                _readOnly = true;
                Warning = "提醒已被另一个实例修改，本实例停止写入和弹出提醒，避免重复或覆盖。请重新打开桌宠。";
                return false;
            }
            Directory.CreateDirectory(_directory);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            if (current is null) File.Move(temporary, _path);
            else File.Replace(temporary, _path, _path + ".bak");
            temporary = null;
            _observed = true;
            _expectedFingerprint = Fingerprint(bytes);
            Warning = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Warning = "提醒未能保存，本次更改未生效；到期消息会保留，写入恢复后再通知。";
            return false;
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private byte[]? ReadBytes()
    {
        if (!File.Exists(_path)) return null;
        if (new FileInfo(_path).Length > 65_536) throw new InvalidDataException("Reminder state too large.");
        return File.ReadAllBytes(_path);
    }
    private static string? Fingerprint(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));
    private static bool Acquire(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromMilliseconds(150)); }
        catch (AbandonedMutexException) { return true; }
    }
}
