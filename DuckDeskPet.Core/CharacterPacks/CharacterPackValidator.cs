using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DuckDeskPet.Core.CharacterPacks;

/// <summary>Offline, fail-closed package checks with no downloads, model execution, or gameplay mutations</summary>
public static partial class CharacterPackValidator
{
    public const int MaximumManifestBytes = 1024 * 1024;
    public const long MaximumTotalResourceBytes = 512L * 1024 * 1024;
    public static readonly ImmutableArray<ClipKind> RequiredActions = [ClipKind.Idle, ClipKind.Yawn, ClipKind.Shy, ClipKind.Eat,
        ClipKind.WorkEnter, ClipKind.WorkLoop, ClipKind.WorkToBusy, ClipKind.BusyLoop, ClipKind.WorkExit, ClipKind.BusyExit,
        ClipKind.HungryEnter, ClipKind.HungryLoop, ClipKind.HungryExit, ClipKind.Tea, ClipKind.RpsRock,
        ClipKind.RpsPaper, ClipKind.RpsScissors, ClipKind.RpsWin, ClipKind.RpsLose, ClipKind.Annoyed];
    public static readonly ImmutableArray<string> RequiredAnchors = ["foot", "head", "body", "bubble", "mouth", "leftHand", "rightHand", "bowl", "keyboard", "deskSurface"];
    public static readonly ImmutableArray<string> SceneLayerOrder = ["fire", "character-body", "desk-back", "character-foreground", "desk-front", "computer"];
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static CharacterPackValidationResult ValidateJson(string json, CharacterValidationMode mode,
        ICharacterPackResources? resources = null, ICharacterModelProbe? modelProbe = null)
    {
        var diagnostics = new List<CharacterPackDiagnostic>();
        void Error(string code, string location, string message) => diagnostics.Add(new(CharacterDiagnosticSeverity.Error, code, location, message));
        CharacterPackManifest? pack;
        try
        {
            if (!Enum.IsDefined(mode)) throw new JsonException("Unsupported validation mode.");
            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes)
                throw new JsonException("Manifest is empty or exceeds the 1 MiB limit.");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicateProperties(document.RootElement, "$");
            foreach (string required in new[] { "version", "kind", "characterId", "displayName", "packVersion", "rigId", "backend", "capabilities", "models", "actions", "parameters", "anchors", "hitAreas", "scene", "outfits", "resources", "sources" })
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(required, out _))
                    throw new JsonException("Missing required package property: " + required);
            if (document.RootElement.GetProperty("actions").ValueKind == JsonValueKind.Object)
                foreach (var action in document.RootElement.GetProperty("actions").EnumerateObject())
                    if (action.Value.ValueKind == JsonValueKind.Object && (!action.Value.TryGetProperty("support", out _) || !action.Value.TryGetProperty("durationSeconds", out _)))
                        throw new JsonException("Action must explicitly declare support and durationSeconds: " + action.Name);
            pack = JsonSerializer.Deserialize<CharacterPackManifest>(json, Json) ?? throw new JsonException("Missing package object.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            Error("JSON_INVALID", "$", ex.Message);
            return new(null, diagnostics.ToImmutableArray(), false, false, false);
        }
        ValidateContract(pack, Error);
        bool contractValid = !diagnostics.Any(x => x.Severity == CharacterDiagnosticSeverity.Error);
        if (!contractValid) return new(pack, diagnostics.ToImmutableArray(), false, false, false);
        if (mode == CharacterValidationMode.AuthorTemplate)
        {
            diagnostics.Add(new(CharacterDiagnosticSeverity.Information, "AUTHOR_ONLY", "$", "Contract only: no model, SDK, texture, animation quality, licensing or runtime readiness has been verified."));
            return new(pack, diagnostics.ToImmutableArray(), true, false, false);
        }
        if (pack.Kind != CharacterPackKind.Runtime)
            Error("TEMPLATE_NOT_RUNTIME", "kind", "An author template cannot authorize playback. Export and verify actual assets first.");
        if (resources is null) Error("RESOURCES_REQUIRED", "resources", "Runtime validation requires a bounded local resource source.");
        if (diagnostics.Any(x => x.Severity == CharacterDiagnosticSeverity.Error))
            return new(pack, diagnostics.ToImmutableArray(), true, false, false);

        var bytes = ValidateFiles(pack, resources!, Error);
        if (!diagnostics.Any(x => x.Severity == CharacterDiagnosticSeverity.Error))
        {
            try { ValidateRuntimeDocuments(pack, bytes, Error); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            { Error("RUNTIME_DOCUMENT_INVALID", "models", ex.Message); }
        }
        bool filesVerified = !diagnostics.Any(x => x.Severity == CharacterDiagnosticSeverity.Error);
        if (!filesVerified) return new(pack, diagnostics.ToImmutableArray(), true, false, false);
        var snapshot = new SnapshotResources(bytes);
        if (modelProbe is null)
        {
            diagnostics.Add(new(CharacterDiagnosticSeverity.Warning, "SDK_PROBE_REQUIRED", "models",
                "Files and references passed offline checks; a trusted backend must load/decode the actual models and textures and verify IDs. RuntimeReady remains false."));
            return new(pack, diagnostics.ToImmutableArray(), true, true, false) { VerifiedResources = snapshot };
        }
        foreach (var (id, _) in pack.Models)
        {
            try
            {
                var inspection = modelProbe.Inspect(pack, id, snapshot);
                if (!inspection.Loaded) { Error("MODEL_LOAD_FAILED", "models." + id, inspection.Detail); continue; }
                foreach (var (semantic, parameter) in pack.Parameters)
                    if (!inspection.Parameters.Contains(parameter.Target)) Error("MODEL_PARAMETER_MISSING", "parameters." + semantic, parameter.Target + " absent in " + id);
                foreach (string drawable in pack.Scene.BodyDrawables.Concat(pack.Scene.ForegroundDrawables).Concat(pack.Anchors.Values.Where(x => x.Target is not null).Select(x => x.Target!)))
                    if (!inspection.Drawables.Contains(drawable)) Error("MODEL_DRAWABLE_MISSING", "models." + id, drawable);
                foreach (var (outfitId, outfit) in pack.Outfits.Where(x => x.Value.Model == id))
                    foreach (string part in outfit.PartOpacity.Keys)
                        if (!inspection.Parts.Contains(part)) Error("MODEL_PART_MISSING", "outfits." + outfitId, part);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { Error("MODEL_PROBE_FAILED", "models." + id, ex.Message); }
        }
        return new(pack, diagnostics.ToImmutableArray(), true, true, !diagnostics.Any(x => x.Severity == CharacterDiagnosticSeverity.Error)) { VerifiedResources = snapshot };
    }

    public static string Serialize(CharacterPackManifest manifest) => JsonSerializer.Serialize(manifest, new JsonSerializerOptions(Json) { WriteIndented = true });

    private static void ValidateContract(CharacterPackManifest p, Action<string, string, string> error)
    {
        void Require(bool condition, string code, string location, string message) { if (!condition) error(code, location, message); }
        if (p.Capabilities is null || p.Models is null || p.Actions is null || p.Parameters is null || p.Anchors is null || p.HitAreas is null ||
            p.Scene is null || p.Outfits is null || p.Resources is null || p.Sources is null)
        { error("NULL_SECTION", "$", "All package sections must be non-null objects."); return; }
        Require(p.Version == 1, "VERSION_UNSUPPORTED", "version", "Only character package contract version 1 is supported.");
        Require(Identifier(p.CharacterId) && Identifier(p.RigId), "IDENTIFIER_INVALID", "characterId/rigId", "Use stable ASCII identifiers, not paths or character-name branching.");
        Require(!string.IsNullOrWhiteSpace(p.DisplayName) && p.DisplayName.Length <= 120, "NAME_INVALID", "displayName", "A bounded display name is required.");
        Require(System.Version.TryParse(p.PackVersion, out _), "PACK_VERSION_INVALID", "packVersion", "Use a numeric dot-separated package version.");
        Require(p.Models.Count is > 0 and <= 16, "MODEL_COUNT", "models", "Supply 1..16 models.");
        Require(p.Outfits.Count is > 0 and <= 32, "OUTFIT_COUNT", "outfits", "Supply 1..32 explicit outfit mappings.");
        Require(p.Resources.Count is > 0 and <= 512, "RESOURCE_COUNT", "resources", "Supply 1..512 declared data resources.");
        Require(p.Parameters.Count <= 128 && p.Anchors.Count <= 64 && p.HitAreas.Count <= 32, "BINDING_LIMIT", "$", "Too many parameter/anchor/hit-area entries.");
        // A JSON null entry must produce a diagnostic, never a dereference or a silent missing default
        if (p.Models.Values.Any(x => x is null) || p.Actions.Values.Any(x => x is null) || p.Parameters.Values.Any(x => x is null) ||
            p.Anchors.Values.Any(x => x is null) || p.HitAreas.Values.Any(x => x is null) || p.Outfits.Values.Any(x => x is null) || p.Resources.Values.Any(x => x is null))
        { error("NULL_ENTRY", "$", "Dictionary entries cannot be null."); return; }
        foreach (var (id, model) in p.Models)
        {
            Require(Identifier(id) && model.RigId == p.RigId, "MODEL_RIG", "models." + id, "Models must explicitly share this package's rig contract.");
            Require(!string.IsNullOrWhiteSpace(model.SdkCompatibility) && model.SdkCompatibility.Length < 120, "SDK_COMPATIBILITY", "models." + id, "Declare the tested backend/export compatibility.");
            Reference(model.Entry, p.Backend == CharacterBackend.Live2D ? CharacterResourceRole.Model : CharacterResourceRole.RasterManifest, "models." + id + ".entry");
        }
        var expected = RequiredActions.Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal);
        Require(expected.SetEquals(p.Actions.Keys), "ACTION_SET", "actions", "Declare Idle and exactly all 19 current semantic actions; unsupported ones must be explicit, not absent.");
        foreach (var (name, action) in p.Actions)
        {
            if (!expected.Contains(name)) continue;
            var kind = Enum.Parse<ClipKind>(name);
            string location = "actions." + name;
            Require(double.IsFinite(action.DurationSeconds) && Math.Abs(action.DurationSeconds - ClipCatalog.GetDefinition(kind).DurationSeconds) < 1e-6,
                "ACTION_DURATION", location, "Duration must match the v1 semantic timeline; no implicit speed scaling.");
            var (phase, entry, exit) = ActionShape(kind);
            Require(action.Phase == phase && action.EntryPose == entry && action.ExitPose == exit, "ACTION_PHASE", location, "Expected " + phase + ": " + entry + " -> " + exit);
            if (action.Support != CharacterActionSupport.Unavailable)
            {
                Require(p.Models.ContainsKey(action.Model ?? ""), "ACTION_MODEL", location, "Action refers to an undeclared model.");
                if (p.Backend == CharacterBackend.Live2D && kind != ClipKind.Idle) Reference(action.Resource, CharacterResourceRole.Motion, location + ".resource");
                else if (action.Resource is not null) Reference(action.Resource, p.Backend == CharacterBackend.Live2D ? CharacterResourceRole.Motion : CharacterResourceRole.RasterManifest, location + ".resource");
            }
            else Require(action.Resource is null, "UNAVAILABLE_RESOURCE", location, "Unavailable actions cannot expose a playable resource.");
            if (action.ControlledParameters.IsDefault) error("NULL_PARAMETERS", location, "controlledParameters must be an array.");
            else foreach (string semantic in action.ControlledParameters)
                Require(semantic is not null && p.Parameters.ContainsKey(semantic), "ACTION_PARAMETER", location, "Unknown semantic parameter: " + semantic);
            bool hold = action.HoldStartSeconds is not null || action.HoldEndSeconds is not null;
            if (hold) Require(action.HoldStartSeconds is double start && action.HoldEndSeconds is double end && double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start && end <= action.DurationSeconds,
                "ACTION_HOLD", location, "The readable pose interval must lie inside the motion.");
            if (kind is ClipKind.RpsRock or ClipKind.RpsPaper or ClipKind.RpsScissors && action.Support == CharacterActionSupport.Complete)
                Require(action.HoldEndSeconds - action.HoldStartSeconds >= .86, "GESTURE_HOLD", location, "Complete guessing gestures need at least 0.86 s of readable hold.");
        }
        Require(p.Actions.TryGetValue("Idle", out var idle) && idle.Support == CharacterActionSupport.Complete, "IDLE_REQUIRED", "actions.Idle", "A complete safe neutral is required.");
        foreach (var (semantic, parameter) in p.Parameters)
        {
            Require(Identifier(semantic) && TargetId(parameter.Target), "PARAMETER_ID", "parameters." + semantic, "Invalid semantic or model parameter ID.");
            Require(double.IsFinite(parameter.Minimum) && double.IsFinite(parameter.Maximum) && double.IsFinite(parameter.Default) && parameter.Minimum < parameter.Maximum && parameter.Default >= parameter.Minimum && parameter.Default <= parameter.Maximum,
                "PARAMETER_RANGE", "parameters." + semantic, "Require finite min < max and an in-range default.");
            Require(parameter.Owner is "motion" or "blink" or "gaze" or "breath" or "outfit", "PARAMETER_OWNER", "parameters." + semantic, "Declare a supported parameter owner.");
        }
        Require(p.Parameters.Values.Select(x => x.Target).Distinct(StringComparer.Ordinal).Count() == p.Parameters.Count, "PARAMETER_ALIAS", "parameters", "Two semantic controls cannot silently own the same model parameter.");
        if (p.Capabilities.EyeBlink) RequiredParameters("eyeLeftOpen", "eyeRightOpen");
        if (p.Capabilities.Gaze) RequiredParameters("gazeX", "gazeY");
        if (p.Capabilities.Breath) RequiredParameters("breath");
        foreach (string anchor in RequiredAnchors) Require(p.Anchors.ContainsKey(anchor), "ANCHOR_MISSING", "anchors." + anchor, "Missing semantic anchor.");
        foreach (var (semantic, anchor) in p.Anchors)
            Require(Identifier(semantic) && Unit(anchor.X) && Unit(anchor.Y) && (anchor.Target is null || TargetId(anchor.Target)), "ANCHOR_INVALID", "anchors." + semantic, "Anchors use normalized top-left coordinates, optional valid drawable ID.");
        if (p.Capabilities.HitTesting) foreach (string area in new[] { "head", "body" }) Require(p.HitAreas.ContainsKey(area), "HIT_AREA_MISSING", "hitAreas." + area, "Missing semantic hit area.");
        foreach (var (id, area) in p.HitAreas)
            Require(Identifier(id) && Unit(area.X) && Unit(area.Y) && double.IsFinite(area.Width) && double.IsFinite(area.Height) && area.Width > 0 && area.Height > 0 && area.X + area.Width <= 1 && area.Y + area.Height <= 1,
                "HIT_AREA_INVALID", "hitAreas." + id, "Hit rectangles must lie inside the normalized canvas.");
        Require(p.Scene.CoordinateSystem == "normalized-top-left", "COORDINATE_SYSTEM", "scene.coordinateSystem", "Only normalized-top-left is supported.");
        Require(p.Scene.UpdateOncePerFrame, "SCENE_CLOCK", "scene.updateOncePerFrame", "All draw passes must share one model update/physics snapshot.");
        Require(!p.Scene.LayerOrder.IsDefault && p.Scene.LayerOrder.SequenceEqual(SceneLayerOrder), "SCENE_ORDER", "scene.layerOrder", "Preserve fire < body < desk < hands < apron < computer.");
        Require(p.Scene.Strategy is "shared-stage" or "split-pass" or "raster-legacy", "SCENE_STRATEGY", "scene.strategy", "Unsupported composition strategy.");
        Require(p.Backend != CharacterBackend.Live2D || p.Scene.Strategy != "raster-legacy", "SCENE_STRATEGY", "scene.strategy", "Live2D cannot claim legacy pixel segmentation.");
        Require(!p.Scene.BodyDrawables.IsDefault && !p.Scene.ForegroundDrawables.IsDefault, "SCENE_DRAWABLES", "scene", "Drawable lists must be arrays.");
        if (!p.Scene.BodyDrawables.IsDefault && !p.Scene.ForegroundDrawables.IsDefault)
        {
            var drawables = p.Scene.BodyDrawables.Concat(p.Scene.ForegroundDrawables).ToArray();
            Require(drawables.All(TargetId) && drawables.Distinct(StringComparer.Ordinal).Count() == drawables.Length && drawables.Length <= 512,
                "SCENE_DRAWABLES", "scene", "Drawable IDs must be distinct, bounded and valid across groups.");
            if (p.Backend == CharacterBackend.Live2D && p.Capabilities.SceneLayers)
                Require(p.Scene.BodyDrawables.Length > 0 && p.Scene.ForegroundDrawables.Length > 0, "SCENE_DRAWABLES", "scene", "Complete scene support requires explicit body and hand/held-prop groups.");
        }
        foreach (var (itemId, outfit) in p.Outfits)
        {
            string location = "outfits." + itemId;
            Require(Identifier(itemId) && p.Models.ContainsKey(outfit.Model ?? ""), "OUTFIT_MODEL", location, "Each outfit must map to a declared compatible model.");
            if (outfit.PartOpacity is null || outfit.ParameterValues is null || outfit.ActionOverrides is null) { error("NULL_OUTFIT", location, "Outfit bindings must be objects."); continue; }
            if (outfit.Thumbnail is not null) Reference(outfit.Thumbnail, CharacterResourceRole.Preview, location + ".thumbnail");
            if (outfit.RasterFallback is not null) Require(CharacterPackPath.IsSafe(outfit.RasterFallback) && outfit.RasterFallback.EndsWith(".json", StringComparison.Ordinal), "FALLBACK_PATH", location, "Fallback is a safe host-resolved raster manifest path, not a download or command.");
            foreach (var (part, opacity) in outfit.PartOpacity) Require(TargetId(part) && Unit(opacity), "OUTFIT_OPACITY", location, "Part visibility must have a valid model ID and 0..1 opacity.");
            foreach (var (semantic, value) in outfit.ParameterValues)
                Require(p.Parameters.TryGetValue(semantic, out var parameter) && double.IsFinite(value) && value >= parameter.Minimum && value <= parameter.Maximum,
                    "OUTFIT_PARAMETER", location, "Unknown or out-of-range semantic parameter: " + semantic);
            foreach (var (action, path) in outfit.ActionOverrides)
            {
                Require(expected.Contains(action) && action != "Idle", "OUTFIT_ACTION", location, "Unknown motion override: " + action);
                Reference(path, CharacterResourceRole.Motion, location + ".actionOverrides." + action);
            }
        }
        Require(p.Capabilities.Outfits || p.Outfits.Count == 1, "OUTFIT_CAPABILITY", "capabilities.outfits", "Multiple outfits require the outfit capability.");
        foreach (var (path, resource) in p.Resources)
        {
            Require(CharacterPackPath.IsSafe(path) && ExtensionAllowed(path, resource.Role), "RESOURCE_PATH", "resources." + path, "Unsafe path or executable/unsupported resource type.");
            Require(resource.ByteLength >= 0 && resource.ByteLength <= MaximumBytes(resource.Role), "RESOURCE_SIZE", "resources." + path, "Declared resource length exceeds role limit.");
            Require(resource.Sha256 is null || Sha256Pattern().IsMatch(resource.Sha256), "RESOURCE_HASH", "resources." + path, "SHA-256 must contain exactly 64 hexadecimal digits.");
        }
        Require(p.Resources.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == p.Resources.Count, "RESOURCE_COLLISION", "resources", "Case-colliding resource paths are not portable.");
        Require(p.Resources.Values.Sum(x => Math.Clamp(x.ByteLength, 0, MaximumTotalResourceBytes)) <= MaximumTotalResourceBytes, "RESOURCE_TOTAL", "resources", "Package exceeds 512 MiB declared runtime data.");
        foreach (var (field, path, extension) in new[] { ("artwork", p.Sources.Artwork, ".psd"), ("modelProject", p.Sources.ModelProject, ".cmo3"), ("animationProject", p.Sources.AnimationProject, ".can3") })
            Require(CharacterPackPath.IsSafe(path) && path.EndsWith(extension, StringComparison.Ordinal), "SOURCE_PATH", "sources." + field, "Declare a safe editable " + extension + " source path; not a runtime script.");
        Require(!string.IsNullOrWhiteSpace(p.Sources.Attribution) && !string.IsNullOrWhiteSpace(p.Sources.License), "PROVENANCE_REQUIRED", "sources", "Record authorship and license status; validation does not grant redistribution rights.");

        void RequiredParameters(params string[] ids) { foreach (string id in ids) Require(p.Parameters.ContainsKey(id), "CAPABILITY_PARAMETER", "parameters." + id, "Capability lacks its semantic binding."); }
        void Reference(string? path, CharacterResourceRole role, string location)
            => Require(path is not null && CharacterPackPath.IsSafe(path) && p.Resources.TryGetValue(path, out var resource) && resource is not null && resource.Role == role,
                "RESOURCE_REFERENCE", location, "Reference must point to a declared " + role + " resource.");
    }

    public static (string Phase, string Entry, string Exit) ActionShape(ClipKind kind) => kind switch
    {
        ClipKind.Idle => ("idle", "standing", "standing"),
        ClipKind.WorkEnter => ("enter", "standing", "working"),
        ClipKind.WorkLoop => ("loop", "working", "working"),
        ClipKind.WorkToBusy => ("transition", "working", "busy"),
        ClipKind.BusyLoop => ("loop", "busy", "busy"),
        ClipKind.WorkExit => ("exit", "working", "standing"),
        ClipKind.BusyExit => ("exit", "busy", "standing"),
        ClipKind.HungryEnter => ("enter", "standing", "hungry"),
        ClipKind.HungryLoop => ("loop", "hungry", "hungry"),
        ClipKind.HungryExit => ("exit", "hungry", "standing"),
        _ => ("once", "standing", "standing")
    };

    private static Dictionary<string, byte[]> ValidateFiles(CharacterPackManifest p, ICharacterPackResources source, Action<string, string, string> error)
    {
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, resource) in p.Resources)
        {
            if (resource.ByteLength <= 0 || resource.Sha256 is null) { error("FILE_IDENTITY_REQUIRED", path, "Runtime files need nonzero exact byte length and SHA-256, not author placeholders."); continue; }
            try
            {
                using var input = source.Open(path);
                if (input is null) { error("FILE_MISSING", path, "Declared resource is absent."); continue; }
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[64 * 1024]; int read;
                while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + read > resource.ByteLength) throw new InvalidDataException("Actual file exceeds declared size.");
                    buffer.Write(chunk, 0, read);
                }
                byte[] bytes = buffer.ToArray();
                if (bytes.LongLength != resource.ByteLength || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(resource.Sha256, StringComparison.OrdinalIgnoreCase))
                { error("FILE_IDENTITY_MISMATCH", path, "Actual size or SHA-256 differs from the manifest."); continue; }
                if (path.EndsWith(".json", StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                    RejectDuplicateProperties(document.RootElement, path);
                }
                else if (resource.Role == CharacterResourceRole.Moc && (bytes.Length < 64 || !bytes.AsSpan(0, 4).SequenceEqual("MOC3"u8)))
                    throw new InvalidDataException("Not a plausible MOC3 container. A signature alone still never proves a real model.");
                else if (resource.Role is CharacterResourceRole.Texture or CharacterResourceRole.Preview)
                {
                    if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                        throw new InvalidDataException("Not a PNG texture container.");
                    uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
                    uint height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
                    if (width is 0 or > 8192 || height is 0 or > 8192) throw new InvalidDataException("Texture dimensions exceed 1..8192.");
                }
                data.Add(path, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { error("FILE_INVALID", path, ex.Message); }
        }
        return data;
    }

    private static void ValidateRuntimeDocuments(CharacterPackManifest p, Dictionary<string, byte[]> bytes, Action<string, string, string> error)
    {
        if (p.Backend != CharacterBackend.Live2D) return; // The trusted raster probe validates legacy PNG suites
        foreach (var (id, model) in p.Models)
        {
            using var doc = JsonDocument.Parse(bytes[model.Entry]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Version", out var version) || !version.TryGetInt32(out int value) || value != 3 ||
                !root.TryGetProperty("FileReferences", out var references) || references.ValueKind != JsonValueKind.Object)
            { error("MODEL_DOCUMENT", model.Entry, "Expected official model3 JSON Version=3 and FileReferences."); continue; }
            if (!references.TryGetProperty("Moc", out var moc) || !references.TryGetProperty("Textures", out var textures) || textures.ValueKind != JsonValueKind.Array || textures.GetArrayLength() == 0)
            { error("MODEL_REFERENCES", model.Entry, "Moc and nonempty Textures are required."); continue; }
            CheckReference(moc, CharacterResourceRole.Moc);
            foreach (var texture in textures.EnumerateArray()) CheckReference(texture, CharacterResourceRole.Texture);
            foreach (var property in references.EnumerateObject())
            {
                if (property.Name is "Moc" or "Textures") continue;
                var role = property.Name switch { "Physics" => CharacterResourceRole.Physics, "Pose" => CharacterResourceRole.Pose,
                    "UserData" or "DisplayInfo" => CharacterResourceRole.Metadata, _ => (CharacterResourceRole?)null };
                if (role is not null) CheckReference(property.Value, role.Value);
                else if (property.Name == "Expressions" && property.Value.ValueKind == JsonValueKind.Array)
                    foreach (var expression in property.Value.EnumerateArray()) CheckFileMember(expression, CharacterResourceRole.Expression);
                else if (property.Name == "Motions" && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var group in property.Value.EnumerateObject())
                    {
                        if (group.Value.ValueKind != JsonValueKind.Array) { error("MODEL_REFERENCES", model.Entry, "Motion groups must be arrays."); continue; }
                        foreach (var motion in group.Value.EnumerateArray())
                        {
                            CheckFileMember(motion, CharacterResourceRole.Motion);
                            if (motion.ValueKind == JsonValueKind.Object && motion.TryGetProperty("Sound", out var sound)) CheckReference(sound, CharacterResourceRole.Audio);
                        }
                    }
                }
                else error("MODEL_REFERENCE_UNSUPPORTED", model.Entry, "Unsupported FileReferences entry: " + property.Name);
            }
            void CheckFileMember(JsonElement element, CharacterResourceRole role)
            {
                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("File", out var file)) CheckReference(file, role);
                else error("MODEL_REFERENCES", model.Entry, "Missing File in " + role + " entry.");
            }
            void CheckReference(JsonElement element, CharacterResourceRole role)
            {
                try
                {
                    if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException("Referenced path must be a string.");
                    string path = CharacterPackPath.ResolveReference(model.Entry, element.GetString()!);
                    if (!p.Resources.TryGetValue(path, out var resource) || resource.Role != role || !bytes.ContainsKey(path))
                        throw new InvalidDataException("Undeclared, wrong-role, or absent referenced " + role + ": " + path);
                }
                catch (InvalidDataException ex) { error("MODEL_REFERENCE_INVALID", model.Entry, ex.Message); }
            }
        }
        foreach (var (name, action) in p.Actions.Where(x => x.Key != "Idle" && x.Value.Support != CharacterActionSupport.Unavailable))
        {
            CheckMotion(action.Resource!, name, action.DurationSeconds);
            foreach (var outfit in p.Outfits.Values)
                if (outfit.ActionOverrides.TryGetValue(name, out var motion)) CheckMotion(motion, name, action.DurationSeconds);
        }
        void CheckMotion(string path, string name, double seconds)
        {
            using var document = JsonDocument.Parse(bytes[path]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Version", out var version) || !version.TryGetInt32(out int v) || v != 3 ||
                !root.TryGetProperty("Meta", out var meta) || meta.ValueKind != JsonValueKind.Object || !meta.TryGetProperty("Duration", out var duration) || !duration.TryGetDouble(out double d) || !double.IsFinite(d) || Math.Abs(d - seconds) > 1e-6 ||
                !root.TryGetProperty("Curves", out var curves) || curves.ValueKind != JsonValueKind.Array || curves.GetArrayLength() == 0)
                error("MOTION_DOCUMENT", path, "A real Version=3 motion with curves and matching duration is required for " + name + ". SDK probing still verifies its semantics.");
        }
    }

    private static bool Unit(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    private sealed class SnapshotResources(Dictionary<string, byte[]> bytes) : ICharacterPackResources
    {
        public Stream? Open(string safeRelativePath)
        {
            if (!CharacterPackPath.IsSafe(safeRelativePath)) throw new InvalidDataException("Unsafe snapshot path.");
            return bytes.TryGetValue(safeRelativePath, out var data) ? new MemoryStream(data, writable: false) : null;
        }
    }
    private static bool Identifier(string? value) => value is not null && IdentifierPattern().IsMatch(value);
    private static bool TargetId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
    private static long MaximumBytes(CharacterResourceRole role) => (role is CharacterResourceRole.Texture or CharacterResourceRole.Moc ? 64L : role is CharacterResourceRole.Model or CharacterResourceRole.RasterManifest ? 2L : 8L) * 1024 * 1024;
    private static bool ExtensionAllowed(string path, CharacterResourceRole role) => role switch
    {
        CharacterResourceRole.Model => path.EndsWith(".model3.json", StringComparison.Ordinal),
        CharacterResourceRole.Moc => path.EndsWith(".moc3", StringComparison.Ordinal),
        CharacterResourceRole.Texture or CharacterResourceRole.Preview => path.EndsWith(".png", StringComparison.Ordinal),
        CharacterResourceRole.Motion => path.EndsWith(".motion3.json", StringComparison.Ordinal),
        CharacterResourceRole.Expression => path.EndsWith(".exp3.json", StringComparison.Ordinal),
        CharacterResourceRole.Physics => path.EndsWith(".physics3.json", StringComparison.Ordinal),
        CharacterResourceRole.Pose => path.EndsWith(".pose3.json", StringComparison.Ordinal),
        CharacterResourceRole.Metadata => path.EndsWith(".cdi3.json", StringComparison.Ordinal) || path.EndsWith(".userdata3.json", StringComparison.Ordinal),
        CharacterResourceRole.Audio => path.EndsWith(".wav", StringComparison.Ordinal),
        CharacterResourceRole.RasterManifest => path.EndsWith(".json", StringComparison.Ordinal),
        _ => false
    };
    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate property at " + path + "." + property.Name);
                RejectDuplicateProperties(property.Value, path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item, path + "[]");
    }
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,95}$", RegexOptions.CultureInvariant)] private static partial Regex IdentifierPattern();
    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex Sha256Pattern();
}
