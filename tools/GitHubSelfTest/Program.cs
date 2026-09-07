using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DuckDeskPet.GitHub;

const string Token = "ghp_" + "SyntheticTestTokenNeverAnActualCredential";
var testRoot = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.codex-build/github-selftest")), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
int passed = 0;
int failed = 0;
int cases = 0;
DateTimeOffset now = DateTimeOffset.Parse("2026-09-06T00:00:00Z");

async Task Test(string name, Func<Task> test)
{
    cases++;
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
}
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
string Folder() => Path.Combine(testRoot, "case-" + cases.ToString("D2") + "-" + Guid.NewGuid().ToString("N"));
HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status)
{ Content = new StringContent(text, Encoding.UTF8, "application/json") };
HttpResponseMessage Snapshot(string body = "[]", string? lastModified = "Sun, 06 Sep 2026 00:00:00 GMT", int poll = 60, string? link = null)
{
    var response = Json(body);
    if (lastModified is not null) response.Content.Headers.LastModified = DateTimeOffset.Parse(lastModified);
    response.Headers.Add("X-Poll-Interval", poll.ToString());
    if (link is not null) response.Headers.TryAddWithoutValidation("Link", link);
    return response;
}
string Item(string id = "1", string time = "2026-09-06T00:00:00Z", string reason = "mention", string kind = "PullRequest", string? comment = "https://api.github.com/repos/test/repo/issues/comments/10", string title = "A synthetic PR") => JsonSerializer.Serialize(new
{
    id, updated_at = time, reason, unread = true,
    subject = new { title, type = kind, url = "https://api.github.com/repos/test/repo/" + (kind == "PullRequest" ? "pulls/42" : "issues/42"), latest_comment_url = comment },
    repository = new { full_name = "test/repo" }
});
GitHubNotificationService Service(FakeHandler handler, string? folder = null) => new(folder ?? Folder(), new HttpClient(handler),
    new WindowsGitHubCredentialProtector(), () => now);
void Account(FakeHandler handler, long id = 1, string login = "test-user") => handler.Add(Json(JsonSerializer.Serialize(new { id, login })));
async Task Seed(GitHubNotificationService service, FakeHandler handler, string body = "[]", int poll = 60)
{ Account(handler); handler.Add(Snapshot(body, poll: poll)); var result = await service.ConnectAsync(Token); Check(result.Success, result.Message); }
async Task ThrowsApi(Func<Task> action, Func<GitHubApiException, bool>? predicate = null)
{
    try { await action(); throw new Exception("Expected GitHubApiException"); }
    catch (GitHubApiException ex) { Check(predicate?.Invoke(ex) ?? true, "Unexpected API exception classification"); }
}

await Test("Token format rejects fine-grained and unsupported credentials", () =>
{
    Check(GitHubApiClient.ValidateTokenFormat("github_pat_" + new string('a', 60))!.Contains("细粒度"));
    foreach (string bad in new[] { "password", "https://github.com", Token + "\r\nInjected: true", "ghs_" + new string('a', 40) })
        Check(GitHubApiClient.ValidateTokenFormat(bad) is not null);
    Check(GitHubApiClient.ValidateTokenFormat(Token) is null);
    Check(GitHubApiClient.ValidateTokenFormat(new string('a', 40)) is null);
    return Task.CompletedTask;
});

await Test("Strict GitHub launch allowlist", () =>
{
    Check(GitHubLinks.TryGetWebUri("https://github.com/test/repo/pull/42#issuecomment-123", out _));
    Check(GitHubLinks.TryGetWebUri("https://github.com/notifications", out _));
    foreach (string bad in new[] { "http://github.com/test/repo/pull/42", "https://github.com.evil.test/test/repo/pull/42", "https://github.com@evil.test/test/repo/pull/42",
        "https://user@github.com/test/repo/pull/42", "https://github.com:444/test/repo/pull/42", "file:///C:/Windows/notepad.exe", "https://github.com/logout",
        "https://github.com/test/repo/pull/42?redirect=evil", "https://github.com/test/repo/pull/42#javascript", "https://github.com/test/repo/pull/42\n" })
        Check(!GitHubLinks.TryGetWebUri(bad, out _), bad);
    return Task.CompletedTask;
});

await Test("Subject-to-PR and comment link conversion", () =>
{
    Check(GitHubLinks.FromSubject("https://api.github.com/repos/test/repo/pulls/42", "https://api.github.com/repos/test/repo/issues/comments/10") == "https://github.com/test/repo/pull/42#issuecomment-10");
    Check(GitHubLinks.FromSubject("https://api.github.com/repos/test/repo/pulls/42", "https://api.github.com/repos/test/repo/pulls/comments/11") == "https://github.com/test/repo/pull/42#discussion_r11");
    Check(GitHubLinks.FromSubject("https://api.github.com/repos/test/repo/issues/42", "https://api.github.com/repos/other/repo/issues/comments/10") == "https://github.com/test/repo/issues/42");
    foreach (string bad in new[] { "https://evil.test/repos/test/repo/pulls/42", "https://api.github.com/repos/test/%2f/pulls/42", "https://api.github.com/repos/test/repo/pulls/42?x=1", "https://api.github.com@evil.test/repos/test/repo/pulls/42" })
        Check(GitHubLinks.FromSubject(bad, null) == "https://github.com/notifications");
    return Task.CompletedTask;
});

await Test("Sticky notification reason labels never claim a fresh mention", () =>
{
    Check(new GitHubInboxItem { Reason = "mention" }.ReasonLabel == "曾提及你的讨论有更新");
    Check(new GitHubInboxItem { Reason = "team_mention" }.ReasonLabel == "曾提及团队的讨论有更新");
    return Task.CompletedTask;
});

await Test("Default relevance filters personal notifications and participating PRs", () =>
{
    foreach (string reason in new[] { "mention", "team_mention", "review_requested", "assign" }) Check(new GitHubInboxItem { Reason = reason }.IsRelevant);
    foreach (string reason in new[] { "author", "comment", "manual" })
    { Check(new GitHubInboxItem { Reason = reason, SubjectType = "PullRequest" }.IsRelevant); Check(!new GitHubInboxItem { Reason = reason, SubjectType = "Issue" }.IsRelevant); }
    Check(!new GitHubInboxItem { Reason = "subscribed", SubjectType = "PullRequest" }.IsRelevant);
    return Task.CompletedTask;
});

await Test("DPAPI current-user round trip never saves plaintext token", () =>
{
    string folder = Folder();
    var store = new GitHubLocalStore(folder, new WindowsGitHubCredentialProtector());
    store.SaveToken(Token);
    byte[] ciphertext = File.ReadAllBytes(Path.Combine(folder, "github-credential.dpapi"));
    Check(!Encoding.UTF8.GetString(ciphertext).Contains(Token));
    Check(store.LoadToken() == Token);
    store.DeleteToken();
    Check(store.LoadToken() is null);
    return Task.CompletedTask;
});

await Test("Constructor is inert: no network, directories or credential read", () =>
{
    string folder = Folder();
    var handler = new FakeHandler();
    using var service = Service(handler, folder);
    Check(!service.IsConnected && !Directory.Exists(folder) && handler.Requests.Count == 0);
    return Task.CompletedTask;
});

await Test("First connect validates both endpoints and seeds unread silently", async () =>
{
    var handler = new FakeHandler();
    using var service = Service(handler);
    await Seed(service, handler, "[" + Item() + "]");
    Check(service.IsConnected && service.Login == "test-user" && service.UnreadCount == 1);
    Check(service.PeekAnnouncement() is null && handler.Requests.Count == 2);
    Check(handler.Requests[0].Url == "https://api.github.com/user");
    Check(handler.Requests.All(r => r.Method == "GET" && r.Authorization == "Bearer " + Token));
    Check(handler.Requests[1].Url.Contains("all=false") && handler.Requests[1].Url.Contains("per_page=50"));
    Check(handler.Requests.All(r => r.Version == "2026-03-10"));
});

await Test("Notifications scope validation failure never saves credentials", async () =>
{
    string folder = Folder();
    var handler = new FakeHandler();
    using var service = Service(handler, folder);
    Account(handler); handler.Add(Json("{\"message\":\"" + Token + "\"}", HttpStatusCode.Forbidden));
    var result = await service.ConnectAsync(Token);
    Check(!result.Success && !service.IsConnected && !File.Exists(Path.Combine(folder, "github-credential.dpapi")));
    Check(!result.Message.Contains(Token) && !service.StatusText.Contains(Token));
});

await Test("Malformed account roots and ID kinds fail cleanly and permit retry", async () =>
{
    foreach (string body in new[]
    {
        "[]", "null", "42", "\"account\"", "{}",
        "{\"id\":\"42\",\"login\":\"x\"}",
        "{\"id\":true,\"login\":\"x\"}",
        "{\"id\":null,\"login\":\"x\"}",
        "{\"id\":1.25,\"login\":\"x\"}",
        "{\"id\":0,\"login\":\"x\"}",
        "{\"id\":9223372036854775808,\"login\":\"x\"}",
        "{\"id\":42,\"login\":[]}"
    })
    {
        string folder = Folder(); var handler = new FakeHandler();
        using var service = Service(handler, folder); handler.Add(Json(body));
        var result = await service.ConnectAsync(Token);
        Check(!result.Success && !service.IsConnecting && !service.IsConnected, body);
        Check(!File.Exists(Path.Combine(folder, "github-credential.dpapi"))
            && !File.Exists(Path.Combine(folder, "github-inbox.json")), "Malformed account persisted files");
        Check(handler.Requests.Count == 1, "Malformed account continued to notifications");
        now += TimeSpan.FromMinutes(1);
        await Seed(service, handler);
        Check(service.IsConnected && !service.IsConnecting, "Service remained stuck after malformed account");
    }
});

await Test("Default 60s minimum poll interval and raw conditional date", async () =>
{
    var handler = new FakeHandler();
    using var service = Service(handler);
    await Seed(service, handler, poll: 1);
    await service.PollNowAsync(); Check(handler.Requests.Count == 2);
    now += TimeSpan.FromSeconds(59); await service.PollNowAsync(); Check(handler.Requests.Count == 2);
    now += TimeSpan.FromSeconds(1); handler.Add(new HttpResponseMessage(HttpStatusCode.NotModified));
    await service.PollNowAsync(); Check(handler.Requests.Count == 3);
    Check(handler.Requests[2].Conditional == "Sun, 06 Sep 2026 00:00:00 GMT");
});

await Test("Server X-Poll-Interval larger than a minute is obeyed including 304", async () =>
{
    var handler = new FakeHandler();
    using var service = Service(handler);
    await Seed(service, handler, poll: 180);
    now += TimeSpan.FromSeconds(179); await service.PollNowAsync(); Check(handler.Requests.Count == 2);
    now += TimeSpan.FromSeconds(1);
    var unchanged = new HttpResponseMessage(HttpStatusCode.NotModified); unchanged.Headers.Add("X-Poll-Interval", "300"); handler.Add(unchanged);
    await service.PollNowAsync(); Check(service.NextPollAt == now + TimeSpan.FromSeconds(300));
});

await Test("New update queues once; announcement acknowledgement does not mark read", async () =>
{
    var handler = new FakeHandler();
    using var service = Service(handler);
    await Seed(service, handler);
    now += TimeSpan.FromMinutes(1); handler.Add(Snapshot("[" + Item() + "]")); await service.PollNowAsync();
    var notice = service.PeekAnnouncement(); Check(notice is not null && service.UnreadCount == 1);
    service.AcknowledgeAnnouncement(notice!.VersionKey);
    Check(service.PeekAnnouncement() is null && service.UnreadCount == 1);
    now += TimeSpan.FromMinutes(1); handler.Add(Snapshot("[" + Item() + "]")); await service.PollNowAsync();
    Check(service.PeekAnnouncement() is null && service.Inbox.Count == 1);
});

await Test("Latest comment changes count even with same updated timestamp", async () =>
{
    var handler = new FakeHandler();
    using var service = Service(handler);
    await Seed(service, handler, "[" + Item() + "]");
    now += TimeSpan.FromMinutes(1); handler.Add(Snapshot("[" + Item(comment: "https://api.github.com/repos/test/repo/issues/comments/11") + "]"));
    await service.PollNowAsync(); Check(service.PeekAnnouncement() is not null);
    Check(service.Inbox[0].WebUrl.EndsWith("#issuecomment-11"));
});

await Test("Local mark-seen suppresses bubble without any GitHub writes", async () =>
{
    var handler = new FakeHandler();
    using var service = Service(handler);
    await Seed(service, handler);
    now += TimeSpan.FromMinutes(1); handler.Add(Snapshot("[" + Item() + "]")); await service.PollNowAsync();
    service.MarkSeen("1"); Check(service.PeekAnnouncement() is null && service.UnreadCount == 0);
    service.MarkAllSeen(); Check(handler.Requests.Count == 3 && handler.Requests.All(r => r.Method == "GET"));
    now += TimeSpan.FromMinutes(1); handler.Add(Snapshot("[" + Item(time: "2026-09-06T01:00:00Z") + "]")); await service.PollNowAsync();
    Check(service.UnreadCount == 1 && service.PeekAnnouncement() is not null);
});

await Test("Opt-in all filter reveals old history silently and persists", async () =>
{
    var handler = new FakeHandler(); string folder = Folder();
    using var service = Service(handler, folder);
    await Seed(service, handler, "[" + Item(reason: "subscribed") + "]");
    Check(service.Inbox.Count == 0);
    service.SetIncludeAllNotifications(true); Check(service.Inbox.Count == 1 && service.PeekAnnouncement() is null);
    Check(JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "github-inbox.json"))).RootElement.GetProperty("IncludeAllNotifications").GetBoolean());
});

await Test("Restart preserves dedup, local seen and pending announcement state", async () =>
{
    string folder = Folder(); var first = new FakeHandler();
    using (var service = Service(first, folder))
    {
        await Seed(service, first);
        now += TimeSpan.FromMinutes(1); first.Add(Snapshot("[" + Item() + "," + Item("2") + "]")); await service.PollNowAsync();
        service.MarkSeen("1");
    }
    var handler = new FakeHandler(); Account(handler); handler.Add(Snapshot("[" + Item() + "," + Item("2") + "]"));
    using var restored = Service(handler, folder); await restored.RestoreAsync();
    Check(restored.IsConnected && restored.UnreadCount == 1 && restored.PeekAnnouncement()?.ThreadId == "2");
    Check(restored.Inbox.Single(i => i.ThreadId == "1").IsSeen);
});

await Test("Changing account clears old inbox and seeds new account silently", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler);
    await Seed(service, handler, "[" + Item() + "]");
    Account(handler, 2, "second-user"); handler.Add(Snapshot("[" + Item("2") + "]"));
    Check((await service.ConnectAsync(Token)).Success);
    Check(service.Login == "second-user" && service.Inbox.Count == 1 && service.Inbox[0].ThreadId == "2" && service.PeekAnnouncement() is null);
});

await Test("Disconnect clears credential and inbox; no future polling", async () =>
{
    string folder = Folder(); var handler = new FakeHandler(); using var service = Service(handler, folder);
    await Seed(service, handler, "[" + Item() + "]"); service.Disconnect();
    Check(!service.IsConnected && service.Login == "" && service.Inbox.Count == 0 && !File.Exists(Path.Combine(folder, "github-credential.dpapi")));
    now += TimeSpan.FromHours(1); await service.PollNowAsync(); Check(handler.Requests.Count == 2);
});

await Test("Disconnect cancels pending fetch and rejects stale response", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler); await Seed(service, handler);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    handler.Add(async (_, _) => { started.SetResult(); return await response.Task; });
    now += TimeSpan.FromMinutes(1); Task poll = service.PollNowAsync(); await started.Task;
    service.Disconnect(); response.SetResult(Snapshot("[" + Item() + "]")); await poll;
    Check(!service.IsConnected && service.Inbox.Count == 0 && service.PeekAnnouncement() is null);
});

await Test("Expired token pauses polling without exposing response text", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler); await Seed(service, handler);
    now += TimeSpan.FromMinutes(1); handler.Add(Json("{\"message\":\"" + Token + "\"}", HttpStatusCode.Unauthorized)); await service.PollNowAsync();
    Check(!service.IsConnected && service.StatusText.Contains("失效") && !service.StatusText.Contains(Token));
    now += TimeSpan.FromHours(2); await service.PollNowAsync(); Check(handler.Requests.Count == 3);
});

await Test("Retry-After and rate-reset backoff are obeyed", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler); await Seed(service, handler);
    now += TimeSpan.FromMinutes(1);
    var limited = Json("{}", HttpStatusCode.Forbidden); limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
    limited.Headers.Add("X-RateLimit-Remaining", "0"); limited.Headers.Add("X-RateLimit-Reset", (now + TimeSpan.FromMinutes(20)).ToUnixTimeSeconds().ToString()); handler.Add(limited);
    await service.PollNowAsync(); Check(service.IsConnected && service.NextPollAt >= now + TimeSpan.FromMinutes(20));
    now += TimeSpan.FromMinutes(19); await service.PollNowAsync(); Check(handler.Requests.Count == 3);
});

await Test("Transient network/server failures exponentially back off", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler); await Seed(service, handler);
    now += TimeSpan.FromMinutes(1); handler.Add(Json("{}", HttpStatusCode.BadGateway)); await service.PollNowAsync();
    Check(service.NextPollAt == now + TimeSpan.FromSeconds(60));
    now += TimeSpan.FromMinutes(1); handler.Add((_, _) => throw new HttpRequestException("DO_NOT_LEAK " + Token)); await service.PollNowAsync();
    Check(service.NextPollAt == now + TimeSpan.FromSeconds(120) && !service.StatusText.Contains(Token));
});

await Test("Redirect response is not followed and pauses connection", async () =>
{
    var handler = new FakeHandler(); using var http = new HttpClient(handler); var api = new GitHubApiClient(http, () => now);
    var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new Uri("https://evil.test/steal"); handler.Add(redirect);
    await ThrowsApi(() => api.GetAccountAsync(Token, default), ex => ex.AuthenticationProblem);
    Check(handler.Requests.Count == 1 && handler.Requests[0].Url == "https://api.github.com/user");
});

await Test("Pagination follows numbered fixed origin and commits full snapshot only", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler); await Seed(service, handler);
    now += TimeSpan.FromMinutes(1);
    handler.Add(Snapshot("[" + Item() + "]", link: "<https://api.github.com/notifications?all=false&per_page=50&page=2>; rel=\"next\""));
    handler.Add(Snapshot("[" + Item("2") + "]")); await service.PollNowAsync();
    Check(service.Inbox.Count == 2 && handler.Requests.Count == 4 && handler.Requests[3].Url.EndsWith("page=2"));
    Check(handler.Requests[2].Conditional is not null && handler.Requests[3].Conditional is null);
});

await Test("Failed later page does not apply partial items or advance watermark", async () =>
{
    string folder = Folder(); var handler = new FakeHandler(); using var service = Service(handler, folder); await Seed(service, handler);
    now += TimeSpan.FromMinutes(1);
    handler.Add(Snapshot("[" + Item() + "]", "Sun, 06 Sep 2026 00:05:00 GMT", link: "<https://api.github.com/notifications?page=2>; rel=\"next\""));
    handler.Add(Json("{}", HttpStatusCode.BadGateway)); await service.PollNowAsync();
    Check(service.Inbox.Count == 0);
    Check(JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "github-inbox.json"))).RootElement.GetProperty("LastModified").GetString() == "Sun, 06 Sep 2026 00:00:00 GMT");
});

await Test("Hostile pagination Link aborts without credential forwarding", async () =>
{
    var handler = new FakeHandler(); using var http = new HttpClient(handler); var api = new GitHubApiClient(http, () => now);
    handler.Add(Snapshot("[" + Item() + "]", link: "<https://evil.test/notifications?page=2>; rel=\"next\""));
    await ThrowsApi(() => api.FetchAsync(Token, null, default)); Check(handler.Requests.Count == 1);
});

await Test("Pagination cap never publishes partial snapshot", async () =>
{
    var handler = new FakeHandler(); using var http = new HttpClient(handler); var api = new GitHubApiClient(http, () => now);
    for (int page = 1; page <= GitHubApiClient.MaximumPages; page++)
        handler.Add(Snapshot("[" + Item(page.ToString()) + "]", link: $"<https://api.github.com/notifications?page={page + 1}>; rel=\"next\""));
    await ThrowsApi(() => api.FetchAsync(Token, null, default), ex => ex.RetryAfter >= TimeSpan.FromMinutes(15));
    Check(handler.Requests.Count == 50);
});

await Test("Response body size is bounded before parsing", async () =>
{
    var handler = new FakeHandler(); using var http = new HttpClient(handler); var api = new GitHubApiClient(http, () => now);
    handler.Add(Json(new string('x', 1_048_577)));
    await ThrowsApi(() => api.FetchAsync(Token, null, default));
});

await Test("Malformed notification response preserves old inbox", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler); await Seed(service, handler, "[" + Item() + "]");
    now += TimeSpan.FromMinutes(1); handler.Add(Snapshot("[{\"id\":\"bad-id\"}]")); await service.PollNowAsync();
    Check(service.Inbox.Count == 1 && service.Inbox[0].ThreadId == "1" && service.IsConnected);
});

await Test("Unread local inbox is bounded to newest 200", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler); Account(handler);
    for (int page = 1; page <= 5; page++)
    {
        string body = "[" + string.Join(',', Enumerable.Range((page - 1) * 50 + 1, 50).Select(i => Item(i.ToString(), DateTimeOffset.Parse("2026-09-06T00:00:00Z").AddSeconds(i).ToString("O")))) + "]";
        handler.Add(Snapshot(body, link: page < 5 ? $"<https://api.github.com/notifications?page={page + 1}>; rel=\"next\"" : null));
    }
    Check((await service.ConnectAsync(Token)).Success);
    Check(service.Inbox.Count == 200 && service.Inbox[0].ThreadId == "250" && service.UnreadCount == 200);
});

await Test("Older duplicate page cannot roll back a newer thread", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler);
    await Seed(service, handler, "[" + Item(time: "2026-09-06T01:00:00Z") + "]");
    now += TimeSpan.FromMinutes(1); handler.Add(Snapshot("[" + Item() + "]")); await service.PollNowAsync();
    Check(service.Inbox[0].UpdatedAt == DateTimeOffset.Parse("2026-09-06T01:00:00Z") && service.PeekAnnouncement() is null);
});

await Test("Corrupt saved inbox is preserved and produces a safe warning", async () =>
{
    string folder = Folder(); Directory.CreateDirectory(folder); string path = Path.Combine(folder, "github-inbox.json");
    File.WriteAllText(path, "{not-json");
    var handler = new FakeHandler(); using var service = Service(handler, folder); await service.RestoreAsync();
    Check(!service.IsConnected && service.StatusText.Contains("保留") && File.ReadAllText(path) == "{not-json" && handler.Requests.Count == 0);
});

await Test("Stored hostile launch URL is filtered on restore", () =>
{
    string folder = Folder(); var store = new GitHubLocalStore(folder, new WindowsGitHubCredentialProtector());
    store.SaveState(new GitHubStoredState { Items = [new() { ThreadId = "1", VersionKey = "a", WebUrl = "https://evil.test" }] });
    Check(store.LoadState().Items.Count == 0);
    return Task.CompletedTask;
});

await Test("Concurrent manual polls are serialized and throttled", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler); await Seed(service, handler);
    now += TimeSpan.FromMinutes(1); handler.Add(Snapshot("[]"));
    await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.PollNowAsync()));
    Check(handler.Requests.Count == 3);
});

await Test("Offline startup resumes saved connection at the bounded retry time", async () =>
{
    string folder = Folder(); var seed = new FakeHandler();
    using (var original = Service(seed, folder)) await Seed(original, seed, "[" + Item() + "]");
    var handler = new FakeHandler(); handler.Add((_, _) => throw new HttpRequestException("offline"));
    using var restored = Service(handler, folder); await restored.RestoreAsync();
    Check(!restored.IsConnected && restored.StatusText.Contains("自动重试") && restored.NextPollAt == now + TimeSpan.FromSeconds(60));
    await restored.PollNowAsync(); Check(handler.Requests.Count == 1);
    now += TimeSpan.FromSeconds(60); Account(handler); handler.Add(Snapshot("[" + Item() + "]")); await restored.PollNowAsync();
    Check(restored.IsConnected && restored.Inbox.Count == 1 && restored.PeekAnnouncement() is null && handler.Requests.Count == 3);
});

await Test("Fresh failed connection never enters stored-account auto retry mode", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler);
    handler.Add((_, _) => throw new HttpRequestException("offline"));
    Check(!(await service.ConnectAsync(Token)).Success && !service.StatusText.Contains("自动重试"));
    now += TimeSpan.FromMinutes(10); await service.PollNowAsync(); Check(handler.Requests.Count == 1);
});

await Test("Manual Connect cannot bypass a GitHub rate-limit delay", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler);
    var limited = Json("{}", HttpStatusCode.TooManyRequests); limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(12)); handler.Add(limited);
    Check(!(await service.ConnectAsync(Token)).Success);
    Check(!(await service.ConnectAsync(Token)).Success && handler.Requests.Count == 1);
    now += TimeSpan.FromMinutes(11); Check(!(await service.ConnectAsync(Token)).Success && handler.Requests.Count == 1);
    now += TimeSpan.FromMinutes(1); await Seed(service, handler); Check(service.IsConnected);
});

await Test("Last successful server interval is retained after a failure", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler); await Seed(service, handler, poll: 600);
    now += TimeSpan.FromMinutes(10); handler.Add(Json("{}", HttpStatusCode.BadGateway)); await service.PollNowAsync();
    Check(service.NextPollAt >= now + TimeSpan.FromMinutes(10));
});

await Test("Later-page failure retains larger interval seen on current snapshot", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler); await Seed(service, handler);
    now += TimeSpan.FromMinutes(1); handler.Add(Snapshot("[]", poll: 900, link: "<https://api.github.com/notifications?page=2>; rel=\"next\""));
    handler.Add(Json("{}", HttpStatusCode.BadGateway)); await service.PollNowAsync();
    Check(service.NextPollAt >= now + TimeSpan.FromMinutes(15));
});

await Test("Bidi and invisible format characters cannot spoof notification titles", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler);
    await Seed(service, handler, "[" + Item(title: "PR\u202eexe\u2066\u200b\nTitle") + "]");
    Check(service.Inbox[0].Title == "PR exe   Title");
});

await Test("Clicking stale version A never marks newer version B seen", async () =>
{
    var handler = new FakeHandler(); using var service = Service(handler); await Seed(service, handler, "[" + Item() + "]");
    string versionA = service.Inbox[0].VersionKey;
    now += TimeSpan.FromMinutes(1); handler.Add(Snapshot("[" + Item(time: "2026-09-06T02:00:00Z") + "]")); await service.PollNowAsync();
    string versionB = service.Inbox[0].VersionKey;
    service.MarkSeen("1", versionA); Check(service.UnreadCount == 1 && service.PeekAnnouncement()?.VersionKey == versionB);
    service.MarkSeen("1", versionB); Check(service.UnreadCount == 0 && service.PeekAnnouncement() is null);
});

await Test("Disabled persisted integration never restores leftover encrypted credential", async () =>
{
    string folder = Folder(); var store = new GitHubLocalStore(folder, new WindowsGitHubCredentialProtector());
    store.SaveToken(Token); store.SaveState(new GitHubStoredState { Enabled = false });
    var handler = new FakeHandler(); using var service = Service(handler, folder); await service.RestoreAsync();
    Check(!service.IsConnected && handler.Requests.Count == 0);
});

await Test("Locked credential cannot prevent saving disabled state during disconnect", async () =>
{
    string folder = Folder(); var handler = new FakeHandler();
    using var service = Service(handler, folder); await Seed(service, handler, "[" + Item() + "]");
    string credentialPath = Path.Combine(folder, "github-credential.dpapi");
    using var credentialLock = new FileStream(credentialPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    service.Disconnect();
    Check(!service.IsConnected && service.Inbox.Count == 0 && File.Exists(credentialPath));
    Check(service.StatusText.Contains("加密凭据未能删除"));
    using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "github-inbox.json")));
    Check(!state.RootElement.GetProperty("Enabled").GetBoolean());
    var restoredHandler = new FakeHandler(); using var restored = Service(restoredHandler, folder);
    await restored.RestoreAsync();
    Check(!restored.IsConnected && restoredHandler.Requests.Count == 0 && restored.Inbox.Count == 0);
});

await Test("Locked inbox cannot prevent deleting credential during disconnect", async () =>
{
    string folder = Folder(); var handler = new FakeHandler();
    using var service = Service(handler, folder); await Seed(service, handler, "[" + Item() + "]");
    using var stateLock = new FileStream(Path.Combine(folder, "github-inbox.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
    service.Disconnect();
    Check(!service.IsConnected && !File.Exists(Path.Combine(folder, "github-credential.dpapi")));
    Check(service.StatusText.Contains("本地消息清理失败"));
    var restoredHandler = new FakeHandler(); using var restored = Service(restoredHandler, folder);
    await restored.RestoreAsync();
    Check(!restored.IsConnected && restoredHandler.Requests.Count == 0);
});

await Test("Both locked files report that next startup may reconnect", async () =>
{
    string folder = Folder(); var handler = new FakeHandler();
    using var service = Service(handler, folder); await Seed(service, handler);
    using var stateLock = new FileStream(Path.Combine(folder, "github-inbox.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
    using var credentialLock = new FileStream(Path.Combine(folder, "github-credential.dpapi"), FileMode.Open, FileAccess.Read, FileShare.Read);
    service.Disconnect();
    Check(!service.IsConnected && service.StatusText.Contains("下次启动可能重新连接"));
    Check(File.Exists(Path.Combine(folder, "github-credential.dpapi")));
    now += TimeSpan.FromHours(1); await service.PollNowAsync(); Check(handler.Requests.Count == 2);
});

Console.WriteLine($"GitHub self-test: {passed} passed, {failed} failed. No live network or user credentials accessed.");
Console.WriteLine("Isolated synthetic test output: " + testRoot);
Environment.ExitCode = failed == 0 ? 0 : 1;

internal sealed record RequestSnapshot(string Url, string Method, string? Authorization, string? Conditional, string? Version);
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();
    public List<RequestSnapshot> Requests { get; } = [];
    public void Add(HttpResponseMessage response) => Add((_, _) => Task.FromResult(response));
    public void Add(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) => _responses.Enqueue(response);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Method.Method, request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("If-Modified-Since", out var date) ? date.First() : null,
            request.Headers.TryGetValues("X-GitHub-Api-Version", out var version) ? version.First() : null));
        if (_responses.Count == 0) throw new InvalidOperationException("Unexpected request: " + request.RequestUri!.GetLeftPart(UriPartial.Path));
        return _responses.Dequeue()(request, cancellationToken);
    }
}
