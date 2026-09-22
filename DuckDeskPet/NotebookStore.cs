using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DuckDeskPet.Core;

namespace DuckDeskPet;

internal interface INotebookPersistence
{
    string? Warning { get; }
    bool CanSave { get; }
    NotebookSnapshot Load();
    bool Save(NotebookSnapshot state);
}

/// <summary>Atomic, bounded notebook file; never accesses pet saves, reminders or credentials</summary>
internal sealed class NotebookStore : INotebookPersistence
{
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private readonly string _directory, _path, _mutexName;
    private string? _expectedFingerprint;
    private bool _observed, _readOnly;
    public string? Warning { get; private set; }
    public bool CanSave => !_readOnly;

    internal NotebookStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        _path = Path.Combine(_directory, "notebook.json");
        _mutexName = "EagleDeskPet.Notebook." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())))[..24];
    }

    public NotebookSnapshot Load()
    {
        using var mutex = new Mutex(false, _mutexName);
        bool acquired = false;
        try
        {
            acquired = Acquire(mutex);
            if (!acquired) throw new IOException("Notebook is busy.");
            byte[]? bytes = ReadBytes();
            var state = bytes is null ? new NotebookSnapshot() : JsonSerializer.Deserialize<NotebookSnapshot>(bytes)
                ?? throw new InvalidDataException("Empty notebook.");
            state = NotebookBoard.Copy(state);
            _expectedFingerprint = Fingerprint(bytes);
            _observed = true;
            Warning = null;
            return state;
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            _readOnly = true;
            Warning = "便签文件无法读取，原文件已保留，暂时只读。请关闭便签墙后检查 notebook.json；不会用空白覆盖旧内容。";
            return new();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    public bool Save(NotebookSnapshot state)
    {
        if (_readOnly) return false;
        string? temporary = null;
        using var mutex = new Mutex(false, _mutexName);
        bool acquired = false;
        try
        {
            NotebookBoard.Validate(state);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, new JsonSerializerOptions { WriteIndented = true });
            if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Notebook is too large.");
            acquired = Acquire(mutex);
            if (!acquired) throw new IOException("Notebook is busy.");
            byte[]? current = ReadBytes();
            if ((!_observed && current is not null) || (_observed && Fingerprint(current) != _expectedFingerprint))
            {
                _readOnly = true;
                Warning = "便签已被另一实例修改。本窗口已停止写入，请关闭并重新打开，避免覆盖新内容。";
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
        catch (Exception ex) when (IsFileError(ex))
        {
            Warning = "便签未能保存，本次更改未生效。内容仍在这里，请检查存储位置后重试。";
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
        if (new FileInfo(_path).Length > MaximumFileBytes) throw new InvalidDataException("Notebook file is too large.");
        byte[] bytes = File.ReadAllBytes(_path);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Notebook grew while reading.");
        return bytes;
    }
    private static bool IsFileError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
    private static string? Fingerprint(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));
    private static bool Acquire(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromMilliseconds(150)); }
        catch (AbandonedMutexException) { return true; }
    }
}
