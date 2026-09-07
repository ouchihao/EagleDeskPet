using System.Text.RegularExpressions;

namespace DuckDeskPet.GitHub;

internal sealed record GitHubConnectionResult(bool Success, string Message);

internal sealed record GitHubInboxItem
{
    public string ThreadId { get; init; } = "";
    public string VersionKey { get; init; } = "";
    public string Title { get; init; } = "";
    public string Repository { get; init; } = "";
    public string Reason { get; init; } = "";
    public string SubjectType { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    public string WebUrl { get; init; } = "https://github.com/notifications";
    public bool IsSeen { get; init; }
    public bool PendingAnnouncement { get; init; }

    // GitHub's reason is sticky per thread, not evidence that every update mentions you again.
    public string ReasonLabel => Reason switch
    {
        "mention" => "曾提及你的讨论有更新",
        "team_mention" => "曾提及团队的讨论有更新",
        "review_requested" => "请求评审的 PR 有动态",
        "author" => "你发起的讨论有更新",
        "comment" => "你参与的讨论有更新",
        "manual" => "你订阅的讨论有更新",
        "assign" => "分配给你的任务有更新",
        _ => "GitHub 有新动态"
    };

    public bool IsRelevant => Reason is "mention" or "team_mention" or "review_requested"
        or "assign" || SubjectType == "PullRequest" && Reason is "author" or "comment" or "manual";
}

internal static class GitHubLinks
{
    private static readonly Regex RepositoryPart = new("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Number = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);
    private static readonly Regex Commit = new("^[0-9a-fA-F]{7,64}$", RegexOptions.CultureInvariant);

    public static bool TryGetWebUri(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)
            || value.Contains('\\') || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps || parsed.Host != "github.com"
            || !parsed.IsDefaultPort || parsed.UserInfo.Length != 0) return false;
        // Only app-produced discussion/notification links may be launched, never settings or logout URLs.
        if (parsed.AbsolutePath == "/notifications" && parsed.Query.Length == 0 && parsed.Fragment.Length == 0)
        { uri = parsed; return true; }
        var parts = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || !GoodRepositoryPart(parts[0]) || !GoodRepositoryPart(parts[1])
            || parsed.Query.Length != 0 || !ValidFragment(parsed.Fragment)) return false;
        if (!(parts[2] is "pull" or "issues" && Number.IsMatch(parts[3]))
            && !(parts[2] == "commit" && Commit.IsMatch(parts[3]))) return false;
        uri = parsed;
        return true;
    }

    public static string FromSubject(string? subjectUrl, string? latestCommentUrl)
    {
        const string fallback = "https://github.com/notifications";
        if (!TryApiParts(subjectUrl, out var parts) || parts.Length != 5 || parts[0] != "repos"
            || !GoodRepositoryPart(parts[1]) || !GoodRepositoryPart(parts[2])) return fallback;
        var route = parts[3] switch { "pulls" => "pull", "issues" => "issues", "commits" => "commit", _ => "" };
        if (route.Length == 0 || !(route == "commit" ? Commit.IsMatch(parts[4]) : Number.IsMatch(parts[4]))) return fallback;
        string result = $"https://github.com/{parts[1]}/{parts[2]}/{route}/{parts[4]}";
        if (TryApiParts(latestCommentUrl, out var comment) && comment.Length == 6 && comment[0] == "repos"
            && string.Equals(comment[1], parts[1], StringComparison.OrdinalIgnoreCase)
            && string.Equals(comment[2], parts[2], StringComparison.OrdinalIgnoreCase)
            && comment[4] == "comments" && Number.IsMatch(comment[5]))
        {
            if (comment[3] == "issues" && route is "pull" or "issues") result += "#issuecomment-" + comment[5];
            else if (comment[3] == "pulls" && route == "pull") result += "#discussion_r" + comment[5];
        }
        return result;
    }

    private static bool TryApiParts(string? value, out string[] parts)
    {
        parts = [];
        if (value is null || value.Length > 2048 || value.Any(char.IsControl) || value.Contains('\\') || value.Contains('%')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host != "api.github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return true;
    }

    private static bool GoodRepositoryPart(string value) => value is not "." and not ".." && RepositoryPart.IsMatch(value);
    private static bool ValidFragment(string value) => value.Length == 0
        || Regex.IsMatch(value, "^#(issuecomment-[0-9]{1,20}|discussion_r[0-9]{1,20})$", RegexOptions.CultureInvariant);
}
