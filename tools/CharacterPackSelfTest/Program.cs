using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DuckDeskPet.Core;
using DuckDeskPet.Core.CharacterPacks;

internal static class Program
{
    private static int _passed, _failed;
    private static readonly string Round = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "round.contract.json"));
    private static readonly string Tall = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tall.contract.json"));

    private static int Main()
    {
        Run("two data-only adapters drive the same unchanged Core timeline", AdapterVariation);
        Run("template validation never authorizes runtime playback", () =>
        {
            var r = Author(Round);
            Check(r.ContractValid && !r.FilesVerified && !r.RuntimeReady && !r.ReadyForCompletePet && !r.CreateAdapter().CanPlay(ClipKind.Shy));
            Check(r.Diagnostics.Any(x => x.Code == "AUTHOR_ONLY"));
            var runtime = CharacterPackValidator.ValidateJson(Round, CharacterValidationMode.Runtime, new MemoryResources());
            Code(runtime, "TEMPLATE_NOT_RUNTIME");
        });
        Run("all six mapped outfits keep semantic actions and normalized anchors", () =>
        {
            foreach (string json in new[] { Round, Tall })
            {
                var adapter = Author(json).CreateAdapter();
                Check(adapter.Manifest.Outfits.Count == 6 && adapter.Manifest.Actions.Count == 20);
                foreach (string outfit in adapter.Manifest.Outfits.Keys)
                    foreach (ClipKind kind in CharacterPackValidator.RequiredActions.Where(x => x != ClipKind.Idle))
                        Check(adapter.ActionResource(kind, outfit) == adapter.Action(kind).Resource);
                Check(adapter.Manifest.Outfits.Values.All(x => x.PartOpacity.Values.Count(v => v == 1) == 1));
            }
        });
        Reject("missing action", n => n["actions"]!.AsObject().Remove("Eat"), "ACTION_SET");
        Reject("unknown legacy action", n => n["actions"]!["Bomb"] = n["actions"]!["Eat"]!.DeepClone(), "ACTION_SET");
        Reject("duration mismatch", n => n["actions"]!["Eat"]!["durationSeconds"] = 1.2, "ACTION_DURATION");
        Reject("missing explicit action support", n => n["actions"]!["Eat"]!.AsObject().Remove("support"), "JSON_INVALID");
        Reject("wrong endpoint pose", n => n["actions"]!["WorkExit"]!["exitPose"] = "working", "ACTION_PHASE");
        Reject("unreadable guessing gesture", n => n["actions"]!["RpsPaper"]!["holdEndSeconds"] = 1.2, "GESTURE_HOLD");
        Reject("missing anchor", n => n["anchors"]!.AsObject().Remove("bubble"), "ANCHOR_MISSING");
        Reject("pixel anchor instead of normalized", n => n["anchors"]!["foot"]!["y"] = 336, "ANCHOR_INVALID");
        Reject("missing blink parameter", n => n["parameters"]!.AsObject().Remove("eyeLeftOpen"), "CAPABILITY_PARAMETER");
        Reject("out of range default", n => n["parameters"]!["mouthOpen"]!["default"] = 7, "PARAMETER_RANGE");
        Reject("missing action parameter", n => n["actions"]!["Eat"]!["controlledParameters"] = new JsonArray("notDeclared"), "ACTION_PARAMETER");
        Reject("null controlled parameter", n => n["actions"]!["Eat"]!["controlledParameters"] = new JsonArray((JsonNode?)null), "ACTION_PARAMETER");
        Reject("bad scene depth", n => n["scene"]!["layerOrder"]![1] = "computer", "SCENE_ORDER");
        Reject("independent body and hand clocks", n => n["scene"]!["updateOncePerFrame"] = false, "SCENE_CLOCK");
        Reject("duplicate scene mesh", n => n["scene"]!["foregroundDrawables"]![0] = "DrawBody", "SCENE_DRAWABLES");
        Reject("unknown outfit model", n => n["outfits"]!["outfit.ox"]!["model"] = "missing", "OUTFIT_MODEL");
        Reject("unsafe host fallback", n => n["outfits"]!["outfit.ox"]!["rasterFallback"] = "../account.json", "FALLBACK_PATH");
        Reject("arbitrary script property", n => n["script"] = "evil.js", "JSON_INVALID");
        Reject("unsupported contract version", n => n["version"] = 2, "VERSION_UNSUPPORTED");
        Reject("missing backend", n => n.AsObject().Remove("backend"), "JSON_INVALID");
        Reject("null section", n => n["anchors"] = null, "NULL_SECTION");
        Reject("null record", n => n["actions"]!["Eat"] = null, "NULL_ENTRY");
        Reject("resource byte budget", n => n["resources"]!["runtime/avatar/character.moc3"]!["byteLength"] = 100_000_000, "RESOURCE_SIZE");
        Reject("malformed SHA", n => n["resources"]!["runtime/avatar/character.moc3"]!["sha256"] = "abc", "RESOURCE_HASH");
        Run("duplicate JSON properties are rejected", () => Code(Author(Round.Replace("\"version\": 1,", "\"version\": 1, \"version\": 1,")), "JSON_INVALID"));
        Run("resource case collisions are rejected", () =>
        {
            var n = JsonNode.Parse(Round)!;
            n["resources"]!["runtime/avatar/CHARACTER.moc3"] = n["resources"]!["runtime/avatar/character.moc3"]!.DeepClone();
            Code(Author(n.ToJsonString()), "RESOURCE_COLLISION");
        });
        foreach (string path in new[] { "../escape.png", "/root.png", "C:/temp/a.png", "a\\b.png", "https://site/a.png", "a//b.png", "a/./b.png", "a/../b.png", "a.png:secret", "a/%2e%2e/b.png", "a/CON.png", "a/LPT1.png", "a/trailing./x.png", "a.js" })
            Reject("unsafe resource " + path, n => n["resources"]![path] = new JsonObject { ["role"] = "texture", ["byteLength"] = 0 }, "RESOURCE_PATH");
        Run("runtime files are mandatory", () =>
        {
            var (pack, _) = RuntimeFixture(Round);
            Code(Validate(pack, new MemoryResources()), "FILE_MISSING");
        });
        Run("actual SHA and declared length are verified", () =>
        {
            var (pack, files) = RuntimeFixture(Round);
            files.Files[pack.Models["main"].Entry][0] ^= 1;
            Code(Validate(pack, files), "FILE_IDENTITY_MISMATCH");
        });
        Run("plausible fake MOC3 is NOT a runtime model", () =>
        {
            var (pack, files) = RuntimeFixture(Round);
            var r = Validate(pack, files);
            Check(r.ContractValid && r.FilesVerified && !r.RuntimeReady && !r.ReadyForCompletePet);
            Code(r, "SDK_PROBE_REQUIRED");
            Code(Validate(pack, files, new RejectingProbe()), "MODEL_LOAD_FAILED");
        });
        Run("invalid MOC container rejected even before SDK probe", () =>
        {
            var (pack, files) = RuntimeFixture(Round);
            pack = ReplaceFile(pack, files, "runtime/avatar/character.moc3", Encoding.UTF8.GetBytes("not a real model"));
            Code(Validate(pack, files), "FILE_INVALID");
        });
        Run("model3 path traversal rejected inside external file", () =>
        {
            var (pack, files) = RuntimeFixture(Round);
            var model = JsonNode.Parse(files.Files[pack.Models["main"].Entry])!;
            model["FileReferences"]!["Moc"] = "../outside.moc3";
            pack = ReplaceFile(pack, files, pack.Models["main"].Entry, Encoding.UTF8.GetBytes(model.ToJsonString()));
            Code(Validate(pack, files), "MODEL_REFERENCE_INVALID");
        });
        Run("model3 unknown resource role is rejected", () =>
        {
            var (pack, files) = RuntimeFixture(Round);
            var model = JsonNode.Parse(files.Files[pack.Models["main"].Entry])!;
            model["FileReferences"]!["Script"] = "evil.js";
            pack = ReplaceFile(pack, files, pack.Models["main"].Entry, Encoding.UTF8.GetBytes(model.ToJsonString()));
            Code(Validate(pack, files), "MODEL_REFERENCE_UNSUPPORTED");
        });
        Run("malformed model fields give diagnostics not exceptions", () =>
        {
            var (pack, files) = RuntimeFixture(Round);
            var model = JsonNode.Parse(files.Files[pack.Models["main"].Entry])!; model["Version"] = "three";
            pack = ReplaceFile(pack, files, pack.Models["main"].Entry, Encoding.UTF8.GetBytes(model.ToJsonString()));
            Check(Validate(pack, files).HasErrors);
        });
        Run("motion duration must match semantic action", () =>
        {
            var (pack, files) = RuntimeFixture(Round);
            string path = pack.Actions["Eat"].Resource!;
            var motion = JsonNode.Parse(files.Files[path])!; motion["Meta"]!["Duration"] = 5;
            pack = ReplaceFile(pack, files, path, Encoding.UTF8.GetBytes(motion.ToJsonString()));
            Code(Validate(pack, files), "MOTION_DOCUMENT");
        });
        Run("trusted probe results verify actual parameter/mesh/part identifiers", () =>
        {
            var (pack, files) = RuntimeFixture(Round);
            Code(Validate(pack, files, new EmptyIdsProbe()), "MODEL_PARAMETER_MISSING");
            Code(Validate(pack, files, new EmptyIdsProbe()), "MODEL_DRAWABLE_MISSING");
            Code(Validate(pack, files, new EmptyIdsProbe()), "MODEL_PART_MISSING");
        });
        Run("verified snapshot cannot be swapped underneath a SDK probe", () =>
        {
            var (pack, files) = RuntimeFixture(Round);
            var r = Validate(pack, files);
            string path = pack.Models["main"].Entry;
            byte[] original = files.Files[path].ToArray(); files.Files[path][0] ^= 1;
            using var verified = r.VerifiedResources!.Open(path)!;
            using var copy = new MemoryStream(); verified.CopyTo(copy);
            Check(copy.ToArray().SequenceEqual(original));
            bool blocked = false; try { r.VerifiedResources.Open("../escape"); } catch (InvalidDataException) { blocked = true; }
            Check(blocked);
        });
        Run("preview-only capability is not a complete pet", () =>
        {
            var p = Author(Round).Manifest!;
            p = p with { Actions = p.Actions.SetItem("Eat", p.Actions["Eat"] with { Support = CharacterActionSupport.Preview }) };
            var r = Author(CharacterPackValidator.Serialize(p)); Check(r.ContractValid && !r.CompleteCharacter);
        });
        Run("filesystem source refuses paths outside the package", () =>
        {
            var files = new DirectoryCharacterPackResources(AppContext.BaseDirectory);
            bool blocked = false; try { files.Open("../CharacterPackSelfTest.dll"); } catch (InvalidDataException) { blocked = true; }
            Check(blocked && files.Open("not-present.png") is null);
        });
        Console.WriteLine($"CharacterPackSelfTest: {_passed} passed, {_failed} failed. Synthetic JSON/MOC bytes are test fixtures, never real Live2D models.");
        return _failed == 0 ? 0 : 1;
    }

    private static void AdapterVariation()
    {
        var adapters = new[] { Author(Round).CreateAdapter(), Author(Tall).CreateAdapter() };
        var transcripts = new List<string[]>();
        foreach (var adapter in adapters)
        {
            // Identical existing Core API calls with different packages and no model-name branch
            var timeline = new ClipTimeline(); timeline.TriggerShy();
            var samples = new List<string>();
            for (int i = 0; i < 60; i++)
            {
                var sample = timeline.Advance(.05);
                var action = adapter.Action(sample.Kind);
                Check(action.Support == CharacterActionSupport.Complete);
                samples.Add($"{sample.Kind}/{sample.Phase}/{sample.Progress:R}/{sample.Sequence}");
            }
            transcripts.Add(samples.ToArray());
            Check(adapter.Manifest.Scene.UpdateOncePerFrame);
            Check(adapter.Parameter("mouthOpen").Maximum == 1);
        }
        Check(transcripts[0].SequenceEqual(transcripts[1]));
        Check(adapters[0].Parameter("mouthOpen").Target != adapters[1].Parameter("mouthOpen").Target);
        Check(adapters[0].Action(ClipKind.Eat).Resource != adapters[1].Action(ClipKind.Eat).Resource);
        Check(adapters[0].Anchor("foot") != adapters[1].Anchor("foot"));
        Check(adapters[0].PlaceAnchor("bubble", 20, 30, 160, 174) != adapters[1].PlaceAnchor("bubble", 20, 30, 160, 174));
    }

    private static CharacterPackValidationResult Author(string json) => CharacterPackValidator.ValidateJson(json, CharacterValidationMode.AuthorTemplate);
    private static CharacterPackValidationResult Validate(CharacterPackManifest p, MemoryResources files, ICharacterModelProbe? probe = null)
        => CharacterPackValidator.ValidateJson(CharacterPackValidator.Serialize(p), CharacterValidationMode.Runtime, files, probe);
    private static void Reject(string name, Action<JsonNode> change, string code) => Run(name, () =>
    {
        var node = JsonNode.Parse(Round)!; change(node); var result = Author(node.ToJsonString());
        Check(!result.ContractValid && !result.RuntimeReady); Code(result, code);
    });
    private static void Code(CharacterPackValidationResult result, string code)
        => Check(result.Diagnostics.Any(x => x.Code == code), "Missing " + code + ": " + string.Join(" | ", result.Diagnostics));
    private static void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
    private static void Run(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }

    private static (CharacterPackManifest, MemoryResources) RuntimeFixture(string json)
    {
        var pack = Author(json).Manifest! with { Kind = CharacterPackKind.Runtime };
        var files = new MemoryResources();
        foreach (var (path, resource) in pack.Resources)
        {
            byte[] data;
            if (resource.Role == CharacterResourceRole.Model)
                data = JsonSerializer.SerializeToUtf8Bytes(new { Version = 3, FileReferences = new { Moc = "character.moc3", Textures = new[] { "textures/atlas.png" } } });
            else if (resource.Role == CharacterResourceRole.Moc)
            { data = new byte[128]; "MOC3"u8.CopyTo(data); } // Intentionally fake, only boundary/probe-negative tests
            else if (resource.Role is CharacterResourceRole.Texture or CharacterResourceRole.Preview)
                data = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");
            else
            {
                var action = pack.Actions.Values.Single(x => x.Resource == path);
                data = JsonSerializer.SerializeToUtf8Bytes(new { Version = 3, Meta = new { Duration = action.DurationSeconds },
                    Curves = new[] { new { Target = "Parameter", Id = pack.Parameters["bodyAngle"].Target, Segments = new[] { 0d, 0d, 0d, action.DurationSeconds, 0d } } } });
            }
            files.Files.Add(path, data);
            pack = pack with { Resources = pack.Resources.SetItem(path, resource with { ByteLength = data.LongLength, Sha256 = Convert.ToHexString(SHA256.HashData(data)) }) };
        }
        return (pack, files);
    }
    private static CharacterPackManifest ReplaceFile(CharacterPackManifest p, MemoryResources files, string path, byte[] data)
    {
        files.Files[path] = data;
        return p with { Resources = p.Resources.SetItem(path, p.Resources[path] with { ByteLength = data.LongLength, Sha256 = Convert.ToHexString(SHA256.HashData(data)) }) };
    }
    private sealed class MemoryResources : ICharacterPackResources
    {
        internal readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
        public Stream? Open(string path) => Files.TryGetValue(path, out var data) ? new MemoryStream(data, false) : null;
    }
    private sealed class RejectingProbe : ICharacterModelProbe
    {
        public CharacterModelInspection Inspect(CharacterPackManifest pack, string id, ICharacterPackResources resources)
            => new(false, "Test-only rejection: bytes are not a real compiled model.", [], [], []);
    }
    private sealed class EmptyIdsProbe : ICharacterModelProbe
    {
        public CharacterModelInspection Inspect(CharacterPackManifest pack, string id, ICharacterPackResources resources)
            => new(true, "Test double for identifier failure paths, not SDK evidence.", [], [], []);
    }
}
