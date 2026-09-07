using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DuckDeskPet.GitHub;

internal interface IGitHubCredentialProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

// No new package dependency; CRYPTPROTECT_UI_FORBIDDEN and no LOCAL_MACHINE flag = current Windows user.
internal sealed class WindowsGitHubCredentialProtector : IGitHubCredentialProtector
{
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public byte[] Protect(byte[] plaintext) => Transform(plaintext, true);
    public byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, false);

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("GitHub 凭据仅支持 Windows 本机加密。");
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect
                ? CryptProtectData(ref input, "EagleDeskPet GitHub", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Windows 无法加密或读取本机 GitHub 凭据。", new Win32Exception(Marshal.GetLastWin32Error()));
            if (output.Length is < 1 or > 16384) throw new CryptographicException("GitHub 凭据大小无效。");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (int i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
}

internal sealed class GitHubStoredState
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public long AccountId { get; set; }
    public string Login { get; set; } = "";
    public bool HasBaseline { get; set; }
    public bool IncludeAllNotifications { get; set; }
    public string? LastModified { get; set; }
    public List<GitHubInboxItem> Items { get; set; } = [];
    public Dictionary<string, string> LatestVersions { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class GitHubLocalStore(string directory, IGitHubCredentialProtector protector)
{
    private readonly string _directory = Path.GetFullPath(directory);
    private string StatePath => Path.Combine(_directory, "github-inbox.json");
    private string CredentialPath => Path.Combine(_directory, "github-credential.dpapi");

    public GitHubStoredState LoadState()
    {
        if (!File.Exists(StatePath)) return new();
        if (new FileInfo(StatePath).Length > 2_097_152) throw new InvalidDataException("GitHub 本地收件箱过大。");
        var state = JsonSerializer.Deserialize<GitHubStoredState>(File.ReadAllText(StatePath))
            ?? throw new InvalidDataException("GitHub 本地收件箱损坏。");
        if (state.Version != 1 || state.Items is null || state.LatestVersions is null || state.Items.Count > 200
            || state.LatestVersions.Count > 5000 || state.Login is null || state.Login.Length > 100
            || state.AccountId < 0 || state.LastModified?.Length > 128) throw new InvalidDataException("GitHub 本地收件箱格式无效。");
        // Disk is not trusted as an external launch target either.
        state.Items = state.Items.Where(i => i is not null && i.ThreadId is { Length: > 0 and <= 64 }
            && i.VersionKey is { Length: > 0 and <= 128 } && i.Title is { Length: <= 500 }
            && i.Repository is { Length: <= 300 } && i.Reason is { Length: <= 100 }
            && i.SubjectType is { Length: <= 100 } && GitHubLinks.TryGetWebUri(i.WebUrl, out _))
            .GroupBy(i => i.ThreadId, StringComparer.Ordinal).Select(g => g.OrderByDescending(i => i.UpdatedAt).First()).ToList();
        state.LatestVersions = state.LatestVersions.Where(p => p.Key is { Length: > 0 and <= 64 }
            && p.Value is { Length: > 0 and <= 128 }).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return state;
    }

    public void SaveState(GitHubStoredState state) => AtomicWrite(StatePath, JsonSerializer.SerializeToUtf8Bytes(state));

    public string? LoadToken()
    {
        if (!File.Exists(CredentialPath)) return null;
        if (new FileInfo(CredentialPath).Length is < 1 or > 16384) throw new InvalidDataException("本机 GitHub 凭据格式无效。");
        byte[] plaintext = protector.Unprotect(File.ReadAllBytes(CredentialPath));
        try { return new UTF8Encoding(false, true).GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void SaveToken(string token)
    {
        byte[] plaintext = Encoding.UTF8.GetBytes(token);
        try { AtomicWrite(CredentialPath, protector.Protect(plaintext)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void DeleteToken() => File.Delete(CredentialPath);

    private void AtomicWrite(string path, byte[] data)
    {
        Directory.CreateDirectory(_directory);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(data); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
