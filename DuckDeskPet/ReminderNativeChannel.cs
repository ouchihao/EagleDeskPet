using DuckDeskPet.Core;

namespace DuckDeskPet;

internal sealed record ReminderNativeResult(bool Accepted, string Detail, bool Uncertain = false);
internal sealed record ReminderNativeActivation(string ReminderId, string CycleId, bool IsTest = false)
{
    internal string Argument => IsTest ? "action=reminder-test" : $"action=reminder&id={ReminderId}&cycle={CycleId}";

    internal static ReminderNativeActivation? Parse(string? argument)
    {
        if (argument == "action=reminder-test") return new("", "", true);
        if (argument is null || argument.Length > 160) return null;
        var parts = argument.Split('&');
        if (parts.Length != 3 || parts[0] != "action=reminder" || !parts[1].StartsWith("id=", StringComparison.Ordinal) ||
            !parts[2].StartsWith("cycle=", StringComparison.Ordinal)) return null;
        string id = parts[1][3..], cycle = parts[2][6..];
        return id.Length == 32 && cycle.Length == 32 && id.All(char.IsAsciiHexDigit) && cycle.All(char.IsAsciiHexDigit) &&
            Guid.TryParseExact(id, "N", out _) && Guid.TryParseExact(cycle, "N", out _) ? new(id, cycle) : null;
    }
}

internal interface IReminderNativeChannel
{
    ReminderNativeResult Prepare() => new(true, "通知通道可用。");
    ReminderNativeResult Send(IReadOnlyList<LocalReminder> batch);
    ReminderNativeResult SendTest();
}

/// <summary>Keeps a partially failed native registration from retaining callbacks</summary>
internal sealed class ReminderRegistrationGate
{
    internal bool IsRegistered { get; private set; }
    private bool _subscribed;
    internal void Ensure(Action subscribe, Action probe, Action unsubscribe)
    {
        if (IsRegistered) return;
        try
        {
            _subscribed = true;
            subscribe();
            probe();
            IsRegistered = true;
        }
        catch
        {
            try { if (_subscribed) unsubscribe(); }
            finally { _subscribed = false; IsRegistered = false; }
            throw;
        }
    }
    internal void Detach(Action unsubscribe)
    {
        try { if (_subscribed) unsubscribe(); }
        finally { _subscribed = false; IsRegistered = false; }
    }
}

/// <summary>App owns the Windows implementation; tests inject a fake with no registry/network access</summary>
internal static class ReminderNotificationServices
{
    internal static IReminderNativeChannel Current { get; set; } = new UnavailableChannel();
    internal static event Action<IReadOnlyList<LocalReminder>, string>? DeliveryFailed;
    internal static void ReportFailure(IReadOnlyList<LocalReminder> batch, string detail) => DeliveryFailed?.Invoke(batch, detail);
    private sealed class UnavailableChannel : IReminderNativeChannel
    {
        public ReminderNativeResult Prepare() => new(false, "Windows 通知服务尚未就绪，暂未启用。");
        public ReminderNativeResult Send(IReadOnlyList<LocalReminder> batch) => new(false, "Windows 通知服务尚未就绪；这轮提醒保留在列表中。");
        public ReminderNativeResult SendTest() => Send([]);
    }
}
