using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace DuckDeskPet;

internal sealed record Live2DLocalRuntime(string SdkRoot, string Framework, string Model, string AssetsRoot,
    string CoreSha256, string FrameworkSha256, string ModelSha256)
{
    internal static Live2DLocalRuntime? Find()
    {
        if (Environment.GetEnvironmentVariable("EAGLE_PET_RENDERER") == "png") return null;
        string? requested = Environment.GetEnvironmentVariable("EAGLE_PET_LIVE2D_CONFIG");
        string? path = requested;
        if (string.IsNullOrWhiteSpace(path))
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, ".tool-cache", "live2d-runtime", "local-runtime.json");
                if (File.Exists(candidate)) { path = candidate; break; }
            }
        }
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidDataException("Live2D 本机配置不存在，请重新运行 prepare-live2d-runtime.ps1");
        var runtime = JsonSerializer.Deserialize<Live2DLocalRuntime>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Live2D 本机配置无效");
        foreach (string value in new[] { runtime.SdkRoot, runtime.Framework, runtime.Model, runtime.AssetsRoot })
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
                throw new InvalidDataException("Live2D 本机配置只接受绝对路径");
        if (!File.ReadAllText(Path.Combine(runtime.SdkRoot, "cubism-info.yml")).Contains("version: 5-r.5", StringComparison.Ordinal))
            throw new InvalidDataException("Live2D SDK 版本与已验证的 Web 5-r.5 不一致");
        CheckHash(Path.Combine(runtime.SdkRoot, "Core", "live2dcubismcore.js"), runtime.CoreSha256);
        CheckHash(runtime.Framework, runtime.FrameworkSha256);
        CheckHash(runtime.Model, runtime.ModelSha256);
        using var provenance = JsonDocument.Parse(File.ReadAllText(runtime.Framework + ".provenance.json"));
        if (provenance.RootElement.GetProperty("sdkRelease").GetString() != "5-r.5" ||
            provenance.RootElement.GetProperty("bundleSha256").GetString() != runtime.FrameworkSha256)
            throw new InvalidDataException("Live2D Framework 来源校验不一致");
        return runtime;
    }

    private static void CheckHash(string path, string expected)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 128 * 1024 * 1024 ||
            !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Live2D 本地文件已变化，请重新准备并验证：" + Path.GetFileName(path));
    }
}
