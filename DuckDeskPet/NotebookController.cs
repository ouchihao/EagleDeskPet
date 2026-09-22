using DuckDeskPet.Core;

namespace DuckDeskPet;

internal readonly record struct NotebookResult(bool Success, bool Changed, string Message);

/// <summary>Commit before publishing, so an animation can never acknowledge an unsaved operation</summary>
internal sealed class NotebookController
{
    private readonly INotebookPersistence _store;
    private readonly TimeProvider _time;
    internal NotebookSnapshot State { get; private set; }
    internal string? Warning => _store.Warning;
    internal bool CanSave => _store.CanSave;

    internal NotebookController(INotebookPersistence store, TimeProvider? time = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
        State = NotebookBoard.Copy(store.Load());
    }

    internal NotebookResult Add(string id, string text) => Commit(() => NotebookBoard.Add(State, id, text, _time.GetUtcNow()), "便签已钉好，只记在这台电脑上。");
    internal NotebookResult Edit(string id, string text) => Commit(() => NotebookBoard.Edit(State, id, text, _time.GetUtcNow()), "这张便签已更新。");
    internal NotebookResult Complete(string id) => Commit(() => NotebookBoard.SetCompleted(State, id, true, _time.GetUtcNow()), "又搞定一件！便签已收进「已完成」，可以恢复。");
    internal NotebookResult Restore(string id) => Commit(() => NotebookBoard.SetCompleted(State, id, false, _time.GetUtcNow()), "便签已重新钉回墙上。");
    // The UI is responsible for explicit confirmation; completing a note never calls Delete
    internal NotebookResult Delete(string id) => Commit(() => NotebookBoard.Delete(State, id), "这张便签已永久删除。");

    private NotebookResult Commit(Func<NotebookChange> operation, string message)
    {
        if (!CanSave) return new(false, false, Warning ?? "便签暂时只读，请重新打开后检查。");
        try
        {
            var candidate = operation();
            if (!candidate.Changed) return new(true, false, "内容没有变化。");
            if (!_store.Save(candidate.Snapshot)) return new(false, false, Warning ?? "没有保存成功，本次更改未生效。");
            State = NotebookBoard.Copy(candidate.Snapshot);
            return new(true, true, message);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { return new(false, false, ex.Message); }
    }
}
