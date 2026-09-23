using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Live2DModelProbe;

internal static class ProbeSelfTest
{
    internal static int Run()
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        string fixture = Path.Combine(temporaryRoot, "EagleLive2DModelProbeTest-" + Guid.NewGuid().ToString("N"));
        Console.WriteLine("ISOLATED TEST ROOT " + fixture);
        Directory.CreateDirectory(fixture);
        int count = 0;
        try
        {
            string sdk = Path.Combine(fixture, "sdk"), models = Path.Combine(fixture, "models"), bundle = Path.Combine(fixture, "framework.js");
            Directory.CreateDirectory(Path.Combine(sdk, "Core"));
            Directory.CreateDirectory(Path.Combine(sdk, "Framework", "src"));
            Directory.CreateDirectory(models);
            string source = Path.Combine(sdk, "Framework", "src", "fixture.ts"), manifest = Path.Combine(models, "fixture.model3.json");
            File.WriteAllText(Path.Combine(sdk, "Core", "live2dcubismcore.js"), "fixture only, not executable Core");
            File.WriteAllText(Path.Combine(sdk, "cubism-info.yml"), "version: 5-r.5\n");
            File.WriteAllText(source, "fixture-source"); File.WriteAllText(bundle, "fixture-bundle"); File.WriteAllText(manifest, "{}");
            string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            void Provenance(string release = "5-r.5") => File.WriteAllText(bundle + ".provenance.json", JsonSerializer.Serialize(new
            {
                sdkRelease = release, sdkInfo = File.ReadAllText(Path.Combine(sdk, "cubism-info.yml")), bundleSha256 = Hash(bundle),
                inputs = new[] { new { name = "Framework/src/fixture.ts", sha256 = Hash(source) } }
            }));
            Provenance();
            string[] Arguments(params string[] extra) => new[] { "--sdk", sdk, "--framework", bundle, "--model", manifest, "--output", Path.Combine(fixture, "evidence") }.Concat(extra).ToArray();
            void Reject(string name, Action action)
            {
                bool rejected = false;
                try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("FAIL: " + name);
                Console.WriteLine("PASS " + name); count++;
            }
            var valid = ProbeOptions.Parse(Arguments("--seconds", "8", "--eagle-contract"));
            if (valid.Seconds != 8 || !valid.EagleContract) throw new InvalidOperationException("Valid fixture rejected");
            count++; Console.WriteLine("PASS matching hashes and explicit contract");
            Reject("invalid integer", () => ProbeOptions.Parse(Arguments("--seconds", "oops")));
            Reject("sampling bound", () => ProbeOptions.Parse(Arguments("--seconds", "61")));
            Reject("duplicate argument", () => ProbeOptions.Parse(Arguments("--sdk", sdk)));
            Reject("relative paths", () => ProbeOptions.Parse(["--sdk", "relative", "--model", manifest, "--output", fixture]));
            File.WriteAllText(bundle, "tampered-bundle");
            Reject("bundle hash mismatch", () => ProbeOptions.VerifyFramework(sdk, bundle));
            Provenance(); File.WriteAllText(source, "tampered-source");
            Reject("SDK source hash mismatch", () => ProbeOptions.VerifyFramework(sdk, bundle));
            Provenance("5-r.4"); Reject("wrong release", () => ProbeOptions.VerifyFramework(sdk, bundle));
            Provenance();
            Directory.CreateDirectory(valid.Output); File.WriteAllText(Path.Combine(valid.Output, "existing.txt"), "keep");
            Reject("existing evidence not overwritten", () => ProbeOptions.Parse(Arguments()));
            Console.WriteLine($"PASS {count} isolated CLI/provenance cases");
            return 0;
        }
        finally
        {
            string resolved = Path.GetFullPath(fixture);
            if (resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("EagleLive2DModelProbeTest-", StringComparison.Ordinal))
                Directory.Delete(resolved, recursive: true);
        }
    }
}
