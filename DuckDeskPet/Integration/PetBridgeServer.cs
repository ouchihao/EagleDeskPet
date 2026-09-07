using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace DuckDeskPet.Integration;

/// <summary>One request per connection. Callbacks run off the UI thread and must honor cancellation.</summary>
public sealed class PetBridgeServer : IDisposable
{
    private readonly Func<PetNotification, CancellationToken, Task<bool>> _onNotification;
    private readonly Func<CancellationToken, Task<object>> _getState;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _notifyGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _accepted = new();
    private readonly Task[] _workers = new Task[4];
    private int _started;
    private int _disposed;

    public PetBridgeServer(Func<PetNotification, CancellationToken, Task<bool>> onNotification,
        Func<CancellationToken, Task<object>> getState)
    {
        _onNotification = onNotification ?? throw new ArgumentNullException(nameof(onNotification));
        _getState = getState ?? throw new ArgumentNullException(nameof(getState));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        for (var i = 0; i < _workers.Length; i++) _workers[i] = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                // Both the SID-scoped name and CurrentUserOnly are intentional. No TCP/HTTP port is opened.
                using var pipe = new NamedPipeServerStream(PetBridgeProtocol.PipeName, PipeDirection.InOut,
                    _workers.Length, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    PetBridgeProtocol.MaxPacketBytes, PetBridgeProtocol.MaxPacketBytes);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(4));
                PetBridgeReply reply;
                try
                {
                    var request = await PetBridgeProtocol.ReadAsync<PetBridgeRequest>(pipe, deadline.Token).ConfigureAwait(false);
                    reply = await HandleAsync(request, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
                {
                    reply = new(false, "invalid_request", "The bridge request is invalid.");
                }
                await PetBridgeProtocol.WriteAsync(pipe, reply, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or InvalidDataException or JsonException or NotSupportedException)
            {
                // A failed local client must not terminate the GUI or create a busy retry loop.
                try { await Task.Delay(250, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private async Task<PetBridgeReply> HandleAsync(PetBridgeRequest request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        if (request.Operation == "get_state" && request.Notification is null)
        {
            try
            {
                var state = await _getState(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                return new(true, "ok", "Local pet state.", state);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new(false, "unavailable", "Pet state is temporarily unavailable.");
            }
        }
        if (request.Operation != "notify") return new(false, "invalid_request", "Unknown bridge operation.");
        var error = PetBridgeProtocol.Validate(request.Notification);
        if (error is not null) return new(false, "invalid_request", error);
        var notification = request.Notification!;
        if (!await _notifyGate.WaitAsync(0, token).ConfigureAwait(false)) return new(false, "busy", "Pet notification queue is busy.");
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _recent.Where(p => now - p.Value > TimeSpan.FromMinutes(10)).Select(p => p.Key).ToArray()) _recent.Remove(key);
            var identity = JsonSerializer.Serialize(new[] { notification.Source, notification.SessionId, notification.EventId });
            if (_recent.ContainsKey(identity)) return new(true, "duplicate", "This event was already accepted; no second bubble was created.");
            while (_accepted.TryPeek(out var time) && now - time >= TimeSpan.FromMinutes(1)) _accepted.Dequeue();
            if (_accepted.Count >= 20 || (_accepted.Count > 0 && now - _accepted.Last() < TimeSpan.FromSeconds(3)))
                return new(false, "rate_limited", "Wait at least 3 seconds between notifications; at most 20 per minute are accepted.");
            bool accepted;
            try { accepted = await _onNotification(notification, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return new(false, "unavailable", "Pet notification handling is temporarily unavailable."); }
            if (!accepted) return new(false, "busy", "Pet notification queue is full or notifications are disabled.");
            if (_recent.Count >= 256) _recent.Remove(_recent.MinBy(p => p.Value).Key);
            _recent[identity] = now;
            _accepted.Enqueue(now);
            return new(true, "accepted", "Notification accepted by the desktop pet.");
        }
        finally { _notifyGate.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        // Do not block the WPF thread while a worker is awaiting its Dispatcher callback.
        _ = Task.WhenAll(_workers.Where(task => task is not null)).ContinueWith(_ =>
        {
            _stop.Dispose();
            _notifyGate.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
