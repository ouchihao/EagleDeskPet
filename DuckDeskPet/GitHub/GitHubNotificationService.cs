using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace DuckDeskPet.GitHub;

/// <summary>Optional read-only GitHub.com inbox. Events may originate on a background thread.</summary>
internal sealed class GitHubNotificationService : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly HttpClient _http;
    private readonly GitHubApiClient _api;
    private readonly GitHubLocalStore _store;
    private readonly Func<DateTimeOffset> _now;
    private readonly bool _automaticPolling;
    private CancellationTokenSource _session = new();
    private GitHubStoredState _state = new();
    private string? _token;
    private string? _restoreToken;
    private string _status = "尚未连接 GitHub";
    private DateTimeOffset? _nextPoll;
    private int _generation;
    private int _failures;
    private TimeSpan _minimumPoll = TimeSpan.FromSeconds(60);
    private DateTimeOffset? _connectNotBefore;
    private bool _disposed;
    private bool _isConnecting;

    public GitHubNotificationService(string dataDirectory)
        : this(dataDirectory, GitHubApiClient.CreateHttpClient(), new WindowsGitHubCredentialProtector(),
            () => DateTimeOffset.UtcNow, true) { }

    internal GitHubNotificationService(string dataDirectory, HttpClient http, IGitHubCredentialProtector protector,
        Func<DateTimeOffset> now, bool automaticPolling = false)
    {
        _http = http;
        _now = now;
        _api = new(http, now);
        _store = new(dataDirectory, protector);
        _automaticPolling = automaticPolling;
    }

    public event Action? Changed;
    public bool IsConnected { get { lock (_sync) return _token is not null; } }
    public bool IsConnecting { get { lock (_sync) return _isConnecting; } }
    public string Login { get { lock (_sync) return _state.Login; } }
    public string StatusText { get { lock (_sync) return _status; } }
    public DateTimeOffset? NextPollAt { get { lock (_sync) return _nextPoll; } }
    public bool IncludeAllNotifications { get { lock (_sync) return _state.IncludeAllNotifications; } }
    public int UnreadCount { get { lock (_sync) return _state.Items.Count(i => !i.IsSeen && Included(i)); } }
    public IReadOnlyList<GitHubInboxItem> Inbox
    { get { lock (_sync) return _state.Items.Where(Included).OrderByDescending(i => i.UpdatedAt).ToArray(); } }

    public async Task RestoreAsync()
    {
        string? token;
        lock (_sync)
        {
            if (_disposed || _isConnecting || _token is not null) return;
            try
            {
                _state = _store.LoadState();
                token = _state.Enabled ? _store.LoadToken() : null;
            }
            catch (Exception ex) when (IsLocalError(ex))
            {
                _status = "GitHub 本地设置或加密凭据无法读取；原文件已保留，请重新连接。";
                token = null;
            }
        }
        RaiseChanged();
        if (token is not null) await ConnectCoreAsync(token, true).ConfigureAwait(false);
    }

    public Task<GitHubConnectionResult> ConnectAsync(string classicToken) => ConnectCoreAsync(classicToken, false);

    private async Task<GitHubConnectionResult> ConnectCoreAsync(string classicToken, bool restoring, int? expectedGeneration = null)
    {
        string token = classicToken.Trim();
        string? formatError = GitHubApiClient.ValidateTokenFormat(token);
        if (formatError is not null) return new(false, formatError);
        int generation;
        CancellationToken cancellation;
        CancellationTokenSource previous;
        lock (_sync)
        {
            if (_disposed) return new(false, "小鹰正在关闭。");
            if (expectedGeneration is not null && expectedGeneration != _generation) return new(false, "连接已取消。");
            if (_connectNotBefore > _now()) return new(false, $"请等待到 {_connectNotBefore.Value.ToLocalTime():HH:mm:ss} 再重试，以遵守 GitHub 的检查间隔。");
            previous = _session;
            _session = new();
            cancellation = _session.Token;
            generation = ++_generation;
            _token = null;
            _restoreToken = restoring ? token : null;
            _nextPoll = null;
            _isConnecting = true;
            _status = "正在验证 GitHub 账户和通知权限…";
        }
        previous.Cancel();
        previous.Dispose();
        RaiseChanged();
        bool entered = false;
        try
        {
            await _operation.WaitAsync(cancellation).ConfigureAwait(false);
            entered = true;
            GitHubAccount account = await _api.GetAccountAsync(token, cancellation).ConfigureAwait(false);
            // A /user success does not prove notifications scope. Verify the actual read endpoint before saving.
            GitHubFetchResult snapshot = await _api.FetchAsync(token, null, cancellation).ConfigureAwait(false);
            lock (_sync)
            {
                if (!IsCurrent(generation)) return new(false, "连接已取消。");
                if (_state.AccountId != account.Id)
                    _state = new() { IncludeAllNotifications = _state.IncludeAllNotifications };
                _state.AccountId = account.Id;
                _state.Login = account.Login;
                _state.Enabled = true;
                ApplySnapshot(snapshot);
                // Only encrypted token bytes ever reach disk. Save state before credential so initial baseline is durable.
                _store.SaveState(_state);
                _store.SaveToken(token);
                _token = token;
                _restoreToken = null;
                _failures = 0;
                _connectNotBefore = null;
                _minimumPoll = snapshot.PollInterval;
                _nextPoll = _now() + snapshot.PollInterval;
                _status = $"已连接 @{account.Login} · 仅检查通知，不会修改 GitHub 已读状态";
                _isConnecting = false;
            }
            RaiseChanged();
            if (_automaticPolling) _ = PollLoopAsync(generation, cancellation);
            return new(true, "GitHub 已连接。现有未读已放入消息列表，新动态将由小鹰提醒。");
        }
        catch (OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested)
                RecordConnectionFailure(generation, cancellation, "GitHub 连接超时，请重试。", null, true);
            return new(false, cancellation.IsCancellationRequested ? "连接已取消。" : "GitHub 连接超时，请重试。");
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException || IsLocalError(ex))
        {
            string message = SafeError(ex);
            bool retryable = ex is HttpRequestException || ex is GitHubApiException { AuthenticationProblem: false };
            RecordConnectionFailure(generation, cancellation, message, (ex as GitHubApiException)?.RetryAfter, retryable);
            return new(false, message);
        }
        finally { if (entered) _operation.Release(); }
    }

    private void RecordConnectionFailure(int generation, CancellationToken cancellation, string message, TimeSpan? retry, bool retryable)
    {
        bool resumeAutomatically;
        lock (_sync)
        {
            if (!IsCurrent(generation)) return;
            _isConnecting = false;
            _status = message;
            if (!retryable) _restoreToken = null;
            _failures = Math.Min(_failures + 1, 8);
            var delay = FailureDelay(retry);
            _connectNotBefore = retryable ? _now() + delay : null;
            _nextPoll = _connectNotBefore;
            resumeAutomatically = _restoreToken is not null;
            if (resumeAutomatically) _status += " 已保存的连接会自动重试。";
        }
        RaiseChanged();
        if (resumeAutomatically && _automaticPolling) _ = PollLoopAsync(generation, cancellation);
    }

    public void Disconnect()
    {
        CancellationTokenSource previous;
        lock (_sync)
        {
            if (_disposed) return;
            previous = _session;
            _session = new();
            ++_generation;
            _token = null;
            _restoreToken = null;
            _isConnecting = false;
            _nextPoll = null;
            _state = new() { IncludeAllNotifications = _state.IncludeAllNotifications };
            _status = "已断开 GitHub，已清除本机凭据和消息列表";
            // Persist the disabled flag first, but attempt both operations independently: either successful
            // step is enough to prevent automatic reconnection if the other file is locked or unwritable.
            bool stateCleared = true;
            bool credentialCleared = true;
            try { _store.SaveState(_state); }
            catch (Exception ex) when (IsLocalError(ex)) { stateCleared = false; }
            try { _store.DeleteToken(); }
            catch (Exception ex) when (IsLocalError(ex)) { credentialCleared = false; }
            if (!stateCleared && !credentialCleared)
                _status = "已停止当前检查，但断开状态和凭据清理均未保存；下次启动可能重新连接。请检查数据目录的写入权限后再次断开。";
            else if (!stateCleared)
                _status = "已断开 GitHub，凭据已删除，但本地消息清理失败；请检查数据目录的写入权限。";
            else if (!credentialCleared)
                _status = "已保存断开状态，不会自动恢复连接，但加密凭据未能删除；请检查数据目录的写入权限后再次断开。";
        }
        previous.Cancel();
        previous.Dispose();
        RaiseChanged();
    }

    public async Task PollNowAsync()
    {
        int generation;
        CancellationToken cancellation;
        string? restoreToken;
        lock (_sync)
        {
            if (_disposed || _token is null && _restoreToken is null || _isConnecting || _nextPoll > _now()) return;
            generation = _generation;
            cancellation = _session.Token;
            restoreToken = _restoreToken;
        }
        if (restoreToken is not null) { await ConnectCoreAsync(restoreToken, true, generation).ConfigureAwait(false); return; }
        await PollCoreAsync(generation, cancellation).ConfigureAwait(false);
    }

    private async Task PollCoreAsync(int generation, CancellationToken cancellation)
    {
        bool entered = false;
        try
        {
            await _operation.WaitAsync(cancellation).ConfigureAwait(false);
            entered = true;
            string token;
            string? lastModified;
            lock (_sync)
            {
                if (!IsCurrent(generation) || _token is null || _nextPoll > _now()) return;
                token = _token;
                lastModified = _state.LastModified;
            }
            var result = await _api.FetchAsync(token, lastModified, cancellation).ConfigureAwait(false);
            lock (_sync)
            {
                if (!IsCurrent(generation)) return;
                ApplySnapshot(result);
                _store.SaveState(_state);
                _failures = 0;
                _minimumPoll = result.PollInterval;
                _nextPoll = _now() + result.PollInterval;
                _status = $"已连接 @{_state.Login} · 最近检查 {_now().ToLocalTime():HH:mm:ss}";
            }
            RaiseChanged();
        }
        catch (OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested) RecordFailure(generation, "GitHub 连接超时，小鹰会稍后重试。", null, false);
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException || IsLocalError(ex))
        { RecordFailure(generation, SafeError(ex), (ex as GitHubApiException)?.RetryAfter, (ex as GitHubApiException)?.AuthenticationProblem == true); }
        finally { if (entered) _operation.Release(); }
    }

    private void RecordFailure(int generation, string message, TimeSpan? retry, bool authenticationProblem)
    {
        lock (_sync)
        {
            if (!IsCurrent(generation)) return;
            _status = message;
            _failures = Math.Min(_failures + 1, 8);
            var delay = FailureDelay(retry);
            _nextPoll = _now() + delay;
            if (authenticationProblem) { _token = null; _restoreToken = null; _nextPoll = null; }
        }
        RaiseChanged();
    }

    private TimeSpan FailureDelay(TimeSpan? retry)
    {
        var delay = TimeSpan.FromSeconds(Math.Min(3600, 60 * Math.Pow(2, _failures - 1)));
        if (_minimumPoll > delay) delay = _minimumPoll;
        if (retry > delay) delay = retry.Value;
        return delay;
    }

    private async Task PollLoopAsync(int generation, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                TimeSpan delay;
                lock (_sync)
                {
                    if (!IsCurrent(generation) || _token is null && _restoreToken is null) return;
                    delay = (_nextPoll ?? _now()) - _now();
                    if (delay > TimeSpan.FromMinutes(1)) delay = TimeSpan.FromMinutes(1);
                }
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation).ConfigureAwait(false);
                // PollNow also retries a previously saved account after an offline startup.
                lock (_sync) if (!IsCurrent(generation)) return;
                await PollNowAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private void ApplySnapshot(GitHubFetchResult snapshot)
    {
        if (snapshot.NotModified) return;
        bool seedSilently = !_state.HasBaseline;
        var known = _state.Items.ToDictionary(i => i.ThreadId, StringComparer.Ordinal);
        foreach (var incoming in snapshot.Items.OrderBy(i => i.UpdatedAt))
        {
            bool duplicate = _state.LatestVersions.TryGetValue(incoming.ThreadId, out var version) && version == incoming.VersionKey;
            // A thread may move across pages while polling. An older page must never roll a newer update back.
            if (known.TryGetValue(incoming.ThreadId, out var old) && incoming.UpdatedAt < old.UpdatedAt) continue;
            if (!duplicate)
                known[incoming.ThreadId] = incoming with { IsSeen = false, PendingAnnouncement = !seedSilently && Included(incoming) };
            _state.LatestVersions[incoming.ThreadId] = incoming.VersionKey;
        }
        _state.Items = known.Values.OrderByDescending(i => i.UpdatedAt).Take(200).ToList();
        // Preserve the current snapshot plus displayed inbox, bounded to 2700 thread versions.
        var keep = snapshot.Items.Select(i => i.ThreadId).Concat(_state.Items.Select(i => i.ThreadId)).ToHashSet(StringComparer.Ordinal);
        _state.LatestVersions = _state.LatestVersions.Where(p => keep.Contains(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        _state.HasBaseline = true;
        _state.LastModified = snapshot.LastModified;
    }

    public void SetIncludeAllNotifications(bool value)
    {
        lock (_sync)
        {
            if (_disposed || _state.IncludeAllNotifications == value) return;
            _state.IncludeAllNotifications = value;
            // Enabling a broader filter reveals history but does not announce it as new.
            SaveStateSafely();
        }
        RaiseChanged();
    }

    public GitHubInboxItem? PeekAnnouncement()
    {
        lock (_sync) return _token is null ? null : _state.Items.Where(i => !i.IsSeen && i.PendingAnnouncement && Included(i))
            .OrderBy(i => i.UpdatedAt).FirstOrDefault();
    }

    public void AcknowledgeAnnouncement(string versionKey)
    {
        lock (_sync)
        {
            if (_disposed) return;
            int index = _state.Items.FindIndex(i => i.VersionKey == versionKey && i.PendingAnnouncement);
            if (index < 0) return;
            _state.Items[index] = _state.Items[index] with { PendingAnnouncement = false };
            SaveStateSafely();
        }
        RaiseChanged();
    }

    public void MarkSeen(string threadId, string? expectedVersion = null)
    {
        lock (_sync)
        {
            if (_disposed) return;
            int index = _state.Items.FindIndex(i => i.ThreadId == threadId);
            if (index < 0 || expectedVersion is not null && _state.Items[index].VersionKey != expectedVersion) return;
            _state.Items[index] = _state.Items[index] with { IsSeen = true, PendingAnnouncement = false };
            SaveStateSafely();
        }
        RaiseChanged();
    }

    public void MarkAllSeen()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _state.Items = _state.Items.Select(i => Included(i) ? i with { IsSeen = true, PendingAnnouncement = false } : i).ToList();
            SaveStateSafely();
        }
        RaiseChanged();
    }

    private bool Included(GitHubInboxItem item) => _state.IncludeAllNotifications || item.IsRelevant;
    private bool IsCurrent(int generation) => !_disposed && generation == _generation;
    private void SaveStateSafely()
    {
        try { _store.SaveState(_state); }
        catch (Exception ex) when (IsLocalError(ex)) { _status = "GitHub 设置暂未保存成功，当前更改仍在内存里。"; }
    }

    private static bool IsLocalError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException
        or CryptographicException or InvalidDataException or PlatformNotSupportedException or System.Text.DecoderFallbackException;
    private static string SafeError(Exception ex) => ex is GitHubApiException api ? api.Message
        : ex is HttpRequestException ? "暂时无法连接 GitHub，请检查网络或代理设置。"
        : "GitHub 本机加密凭据或收件箱保存失败，请检查数据目录权限后重试。";

    private void RaiseChanged()
    {
        // UI subscribers must marshal to their dispatcher. A closed window must not fault the background worker.
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
        { try { handler(); } catch (InvalidOperationException) { } }
    }

    public void Dispose()
    {
        CancellationTokenSource session;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ++_generation;
            _token = null;
            _restoreToken = null;
            session = _session;
        }
        session.Cancel();
        session.Dispose();
        _http.Dispose();
        // Do not dispose the semaphore while a cancelled request still needs to release it.
    }
}
