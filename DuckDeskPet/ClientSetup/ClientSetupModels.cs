namespace DuckDeskPet.ClientSetup;

public enum ClientKind { Codex, ClaudeCode, CodeBuddyCode }
public enum SetupAction { Install, Remove }
public sealed record SetupFilePreview(string Path, string Description, bool WillChange);
public sealed record ClientSetupResult(bool Success, string Message, IReadOnlyList<string> BackupPaths);

public sealed class ClientSetupPlan
{
    public ClientKind Client { get; internal init; }
    public SetupAction Action { get; internal init; }
    public bool CanApply { get; internal init; }
    public bool HasChanges => Files.Any(file => file.WillChange);
    public string Status { get; internal init; } = "";
    public IReadOnlyList<string> Notes { get; internal init; } = [];
    public IReadOnlyList<SetupFilePreview> Files { get; internal init; } = [];
    internal Guid ServiceId { get; init; }
    internal IReadOnlyList<SetupFileChange> Changes { get; init; } = [];
}

internal sealed record SetupFileChange(string Path, string Description, byte[]? Before, byte[] After)
{
    public bool Changed => Before is null || !Before.AsSpan().SequenceEqual(After);
}
