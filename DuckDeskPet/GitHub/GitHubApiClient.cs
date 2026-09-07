using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DuckDeskPet.GitHub;

internal sealed record GitHubAccount(long Id, string Login);
internal sealed record GitHubFetchResult(bool NotModified, IReadOnlyList<GitHubInboxItem> Items,
    string? LastModified, TimeSpan PollInterval);

internal sealed class GitHubApiException(string message, bool authenticationProblem = false, TimeSpan? retryAfter = null)
    : Exception(message)
{
    public bool AuthenticationProblem { get; } = authenticationProblem;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal sealed class GitHubApiClient(HttpClient http, Func<DateTimeOffset> now)
{
    internal const int MaximumPages = 50;
    private const int MaximumResponseBytes = 1_048_576;
    private static readonly Uri ApiBase = new("https://api.github.com/");

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static string? ValidateTokenFormat(string token)
    {
        if (token.StartsWith("github_pat_", StringComparison.Ordinal))
            return "通知接口不支持细粒度 Token，请创建 Personal access token (classic)，勾选 notifications。";
        if (!Regex.IsMatch(token, "^(ghp_[A-Za-z0-9]{20,200}|[a-fA-F0-9]{40})$", RegexOptions.CultureInvariant))
            return "请粘贴 GitHub Personal access token (classic)，不要输入密码、细粒度 Token 或网页地址。";
        return null;
    }

    public async Task<GitHubAccount> GetAccountAsync(string token, CancellationToken cancellation)
    {
        using var response = await SendAsync("user", token, null, cancellation).ConfigureAwait(false);
        ThrowIfError(response);
        JsonDocument document;
        try { document = await ReadJsonAsync(response, cancellation).ConfigureAwait(false); }
        catch (IOException) { throw new GitHubApiException("GitHub 网络连接中断，小鹰会稍后重试。"); }
        using var json = document;
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out long id) || id <= 0
            || !root.TryGetProperty("login", out var loginElement) || loginElement.ValueKind != JsonValueKind.String)
            throw new GitHubApiException("GitHub 返回的账户格式无效，暂未连接。");
        string login = loginElement.GetString() ?? "";
        if (!Regex.IsMatch(login, "^[A-Za-z0-9-]{1,100}$", RegexOptions.CultureInvariant))
            throw new GitHubApiException("GitHub 返回的账户名称无效，暂未连接。");
        return new(id, login);
    }

    public async Task<GitHubFetchResult> FetchAsync(string token, string? lastModified, CancellationToken cancellation)
    {
        var items = new List<GitHubInboxItem>();
        string? nextLastModified = null;
        var pollInterval = TimeSpan.FromSeconds(60);
        try
        {
        for (int page = 1; page <= MaximumPages; page++)
        {
            // Construct URLs ourselves. Never follow an untrusted pagination URL with an Authorization header.
            string route = $"notifications?all=false&participating=false&per_page=50&page={page}";
            using var response = await SendAsync(route, token, page == 1 ? lastModified : null, cancellation).ConfigureAwait(false);
            pollInterval = Maximum(pollInterval, ReadPollInterval(response));
            if (response.StatusCode == HttpStatusCode.NotModified && page == 1 && lastModified is not null)
                return new(true, [], lastModified, pollInterval);
            ThrowIfError(response);
            if (page == 1) nextLastModified = ReadLastModified(response);
            using var json = await ReadJsonAsync(response, cancellation).ConfigureAwait(false);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 50)
                throw new GitHubApiException("GitHub 返回的通知格式无效，稍后重试。");
            foreach (var item in json.RootElement.EnumerateArray()) items.Add(ParseNotification(item));
            if (!HasNextPage(response, page))
                return new(false, items, nextLastModified, pollInterval);
        }
        // Never advance the conditional watermark or publish a partial snapshot.
        throw new GitHubApiException("GitHub 未读超过 2500 条，本次未完整读取；请先在 GitHub 整理旧通知后重试。",
            retryAfter: TimeSpan.FromMinutes(15));
        }
        catch (GitHubApiException ex)
        {
            throw new GitHubApiException(ex.Message, ex.AuthenticationProblem,
                Maximum(pollInterval, ex.RetryAfter ?? TimeSpan.Zero));
        }
        catch (HttpRequestException)
        { throw new GitHubApiException("暂时无法连接 GitHub，请检查网络或代理设置。", retryAfter: pollInterval); }
        catch (IOException)
        { throw new GitHubApiException("GitHub 网络连接中断，小鹰会稍后重试。", retryAfter: pollInterval); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new GitHubApiException("GitHub 连接超时，小鹰会稍后重试。", retryAfter: pollInterval); }
    }

    private async Task<HttpResponseMessage> SendAsync(string route, string token, string? lastModified, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, route));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("EagleDeskPet/1.8.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        if (lastModified is not null && DateTimeOffset.TryParse(lastModified, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out _)) request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new GitHubApiException("GitHub 返回的数据过大，已停止读取。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > MaximumResponseBytes) throw new GitHubApiException("GitHub 返回的数据过大，已停止读取。");
            buffer.Write(chunk, 0, count);
        }
        try { return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw new GitHubApiException("GitHub 返回的数据暂时无法解析，稍后重试。"); }
    }

    private void ThrowIfError(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.OK) return;
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
            throw new GitHubApiException("GitHub 请求发生了重定向，已为保护凭据停止连接。", true);
        if (status == 401) throw new GitHubApiException("GitHub 凭据已失效或被撤销，请重新连接。", true);
        TimeSpan? retry = ReadRetryAfter(response);
        bool limit = status == 429 || status == 403 && (retry is not null || Header(response, "X-RateLimit-Remaining") == "0");
        if (limit) throw new GitHubApiException("GitHub 暂时限流，小鹰会等待后自动重试。", retryAfter: retry ?? TimeSpan.FromMinutes(5));
        if (status == 403) throw new GitHubApiException("GitHub 权限不足：请检查 classic Token 的 notifications 权限，以及组织的 SSO 授权。", true);
        if (status >= 500) throw new GitHubApiException("GitHub 暂时不可用，小鹰会自动重试。");
        throw new GitHubApiException("GitHub 暂时未接受通知请求，请稍后重试。", retryAfter: retry);
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        TimeSpan? delay = response.Headers.RetryAfter?.Delta;
        if (response.Headers.RetryAfter?.Date is { } date) delay = Maximum(TimeSpan.Zero, date - now());
        if (Header(response, "X-RateLimit-Remaining") == "0"
            && long.TryParse(Header(response, "X-RateLimit-Reset"), out long reset))
        {
            try { delay = Maximum(delay ?? TimeSpan.Zero, DateTimeOffset.FromUnixTimeSeconds(reset) - now() + TimeSpan.FromSeconds(1)); }
            catch (ArgumentOutOfRangeException) { delay = TimeSpan.FromHours(1); }
        }
        return delay is null ? null : Maximum(TimeSpan.FromSeconds(60), delay.Value);
    }

    private static bool HasNextPage(HttpResponseMessage response, int page)
    {
        string? link = Header(response, "Link");
        if (link is null) return false;
        if (link.Length > 8192) throw new GitHubApiException("GitHub 分页信息无效，稍后重试。");
        foreach (string piece in link.Split(','))
        {
            if (!Regex.IsMatch(piece, ";\\s*rel=\"?next\"?(?:\\s*;|\\s*$)", RegexOptions.CultureInvariant)) continue;
            var match = Regex.Match(piece, "^\\s*<([^>]+)>", RegexOptions.CultureInvariant);
            if (!match.Success || !Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri)
                || uri.Scheme != "https" || uri.Host != "api.github.com" || !uri.IsDefaultPort
                || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/notifications" || uri.Fragment.Length != 0
                || !Regex.IsMatch(uri.Query, $"(?:[?&])page={page + 1}(?:&|$)", RegexOptions.CultureInvariant))
                throw new GitHubApiException("GitHub 分页目标异常，已停止本次读取以保护凭据。");
            return true;
        }
        return false;
    }

    private static string? ReadLastModified(HttpResponseMessage response)
    {
        string? value = Header(response, "Last-Modified")
            ?? (response.Content.Headers.TryGetValues("Last-Modified", out var values) ? values.FirstOrDefault() : null);
        return value is { Length: <= 128 } && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out _) ? value : null;
    }

    private static TimeSpan ReadPollInterval(HttpResponseMessage response) =>
        int.TryParse(Header(response, "X-Poll-Interval"), out int seconds) && seconds > 60
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(60);
    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    private static TimeSpan Maximum(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static GitHubInboxItem ParseNotification(JsonElement item)
    {
        try
        {
            string id = item.GetProperty("id").GetString() ?? "";
            string updatedText = item.GetProperty("updated_at").GetString() ?? "";
            if (!Regex.IsMatch(id, "^[0-9]{1,64}$", RegexOptions.CultureInvariant)
                || !DateTimeOffset.TryParse(updatedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updated))
                throw new FormatException();
            var subject = item.GetProperty("subject");
            string? subjectUrl = OptionalString(subject, "url");
            string? latestComment = OptionalString(subject, "latest_comment_url");
            if (subjectUrl?.Length > 2048 || latestComment?.Length > 2048) throw new FormatException();
            string version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + "\n" + updated.ToUniversalTime().ToString("O") + "\n" + latestComment)));
            return new()
            {
                ThreadId = id, VersionKey = version, UpdatedAt = updated,
                Title = Clean(OptionalString(subject, "title"), 500),
                SubjectType = Clean(OptionalString(subject, "type"), 100),
                Repository = Clean(OptionalString(item.GetProperty("repository"), "full_name"), 300),
                Reason = Clean(OptionalString(item, "reason"), 100),
                WebUrl = GitHubLinks.FromSubject(subjectUrl, latestComment)
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new GitHubApiException("GitHub 返回的通知字段不完整，稍后重试。"); }
    }

    private static string? OptionalString(JsonElement item, string name) => item.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Clean(string? value, int maximum) => new((value ?? "").Take(maximum)
        .Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format ? ' ' : c).ToArray());
}
