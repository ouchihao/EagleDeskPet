// SPDX-License-Identifier: GPL-3.0-or-later
import io.github.psd2live.core.*;
import kotlinx.serialization.json.*;
import org.umamo.format.art.SourceLayer;
import org.umamo.runtime.model.Drawable;
import org.umamo.runtime.model.KeyformAxis;
import org.umamo.runtime.model.Parameter;
import java.lang.reflect.Method;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.util.*;

// Offline authoring adapter for externally installed PSD2Live 1.1.1
// The desktop application does not load this Java code or the GPL authoring library
public final class ExportPsdModel {
    private static final int CONFIG_COMPONENTS = 46;

    public static void main(String[] args) throws Exception {
        if (args.length != 3) throw new IllegalArgumentException(
            "Usage: ExportPsdModel.java input.psd fresh-output-directory rig-profile.json");
        Path input = Path.of(args[0]).toRealPath();
        Path output = Path.of(args[1]).toAbsolutePath().normalize();
        Path profilePath = Path.of(args[2]).toRealPath();
        if (!Files.isRegularFile(input) || !input.getFileName().toString().toLowerCase(Locale.ROOT).endsWith(".psd"))
            throw new IllegalArgumentException("Input must be an existing layered PSD");
        if (Files.exists(output)) throw new IllegalArgumentException("Refusing to overwrite an existing output directory");
        for (Path parent = output.getParent(); parent != null; parent = parent.getParent())
            if (Files.isSymbolicLink(parent)) throw new IllegalArgumentException("Output ancestor is a symbolic link");
        System.out.println("Input: " + input);
        System.out.println("Output: " + output);
        System.out.println("Profile: " + profilePath);

        JsonObject profile = object(Json.Default.parseToJsonElement(Files.readString(profilePath)), "profile");
        keys(profile, Set.of("schemaVersion", "psd2liveVersion", "expectedSourceLayers", "config", "layers", "requiredParameters"));
        if (integer(profile.get("schemaVersion"), "schemaVersion") != 1 || !string(profile.get("psd2liveVersion"), "psd2liveVersion").equals("1.1.1"))
            throw new IllegalArgumentException("This adapter requires schemaVersion 1 and PSD2Live 1.1.1");
        var pipeline = new PSD2LivePipeline();
        var base = new PipelineConfig();
        var analysis = pipeline.inspect(input, base);
        if (analysis.getSource().getLayers().size() != integer(profile.get("expectedSourceLayers"), "expectedSourceLayers"))
            throw new IllegalArgumentException("Source layer count differs from reviewed profile");

        var overrides = new LinkedHashMap<String, LayerClassificationOverride>();
        var parents = new LinkedHashMap<String, String>();
        var expectedEyeBindings = new LinkedHashMap<String, String>();
        var eyeClosures = new LinkedHashMap<String, JsonObject>();
        var names = new HashSet<String>();
        for (JsonElement item : array(profile.get("layers"), "layers")) {
            JsonObject rule = object(item, "layer rule");
            keys(rule, Set.of("name", "semanticTag", "side", "parent", "requiredGeometryParameter", "closedEye"));
            String name = string(rule.get("name"), "layer name");
            if (!names.add(name)) throw new IllegalArgumentException("Duplicate profile layer: " + name);
            var matches = analysis.getSource().getLayers().stream().filter(layer -> layer.getName().equals(name)).toList();
            if (matches.size() != 1) throw new IllegalArgumentException("Expected exactly one source layer: " + name);
            SourceLayer source = matches.getFirst();
            String sourceId = mangledString(source, SourceLayer.class, "getId-");
            if (rule.containsKey("semanticTag")) {
                SemanticTag tag = SemanticTag.valueOf(string(rule.get("semanticTag"), "semanticTag"));
                Side side = Side.valueOf(string(rule.get("side"), "side"));
                overrides.put(sourceId, new LayerClassificationOverride(tag, side));
            } else if (rule.containsKey("side")) throw new IllegalArgumentException("side requires semanticTag");
            if (rule.containsKey("parent")) {
                if (rule.get("parent") != JsonNull.INSTANCE) throw new IllegalArgumentException("Only explicit null/root parent is supported");
                parents.put(sourceId, null);
            }
            if (rule.containsKey("requiredGeometryParameter"))
                expectedEyeBindings.put(sourceId, string(rule.get("requiredGeometryParameter"), "requiredGeometryParameter"));
            if (rule.containsKey("closedEye")) {
                if (!rule.containsKey("requiredGeometryParameter")) throw new IllegalArgumentException("closedEye requires requiredGeometryParameter");
                JsonObject closure = object(rule.get("closedEye"), "closedEye");
                keys(closure, Set.of("heightPx", "curveDepthPx"));
                positivePixel(closure.get("heightPx"), "heightPx");
                positivePixel(closure.get("curveDepthPx"), "curveDepthPx");
                eyeClosures.put(sourceId, closure);
            }
            System.out.println("Layer rule: " + name + " => " + sourceId + " " + rule);
        }

        PipelineConfig config = configure(base, object(profile.get("config"), "config"), overrides, parents);
        var preview = pipeline.buildPreview(input, config);
        if (!eyeClosures.isEmpty()) {
            RigEditOverlay edits = new RigEditOverlay();
            for (Drawable drawable : preview.getRig().getPuppet().getDrawables()) {
                String drawableId = mangledString(drawable, Drawable.class, "getId-");
                String sourceId = preview.getRig().getLayerIdByDrawableId().get(drawableId);
                if (!eyeClosures.containsKey(sourceId)) continue;
                JsonObject closure = eyeClosures.get(sourceId);
                String parameter = expectedEyeBindings.get(sourceId);
                var grid = drawable.getGeometryGrid();
                if (grid == null || grid.getAxes().size() != 1 || !parameter.equals(mangledString(grid.getAxes().getFirst(), KeyformAxis.class, "getParameterId-")))
                    throw new IllegalStateException("Eye mesh is not bound exclusively to " + parameter);
                SourceLayer source = null;
                for (SourceLayer layer : analysis.getSource().getLayers())
                    if (sourceId.equals(mangledString(layer, SourceLayer.class, "getId-"))) source = layer;
                if (source == null) throw new IllegalStateException("Missing original eye raster");
                float[] positions = drawable.getMesh().getPositions();
                float minX = Float.POSITIVE_INFINITY, minY = Float.POSITIVE_INFINITY;
                float maxX = Float.NEGATIVE_INFINITY, maxY = Float.NEGATIVE_INFINITY;
                for (int i = 0; i < positions.length; i += 2) {
                    minX = Math.min(minX, positions[i]); maxX = Math.max(maxX, positions[i]);
                    minY = Math.min(minY, positions[i + 1]); maxY = Math.max(maxY, positions[i + 1]);
                }
                if (!(maxX > minX && maxY > minY)) throw new IllegalStateException("Degenerate eye mesh");
                float sourceHeight = source.getRaster().getHeight();
                float closeScale = positivePixel(closure.get("heightPx"), "heightPx") / sourceHeight;
                float curve = positivePixel(closure.get("curveDepthPx"), "curveDepthPx") * (maxY - minY) / sourceHeight;
                float centerY = (minY + maxY) * 0.5f;
                var target = new RigTargetRef(RigTargetKind.ART_MESH, drawableId, null);
                for (float openness : new float[] {0f, 0.5f, 1f}) {
                    var delta = new ArrayList<Float>(positions.length);
                    for (int i = 0; i < positions.length; i += 2) {
                        float u = 2f * (positions[i] - minX) / (maxX - minX) - 1f;
                        float closedY = centerY + curve * (1f - u * u) + (positions[i + 1] - centerY) * closeScale;
                        delta.add(0f);
                        delta.add((closedY - positions[i + 1]) * (1f - openness));
                    }
                    edits = edits.setKeyform(new RigKeyformSetEdit(target, Map.of(parameter, openness),
                        new RigKeyformGeometryEdit(null, null, null, null, null, delta), null));
                }
                System.out.println("Authored eye closure: " + drawableId + " -> " + parameter + " @ 0 / 0.5 / 1, no opacity fade");
            }
            config = withEdits(config, edits);
            preview = pipeline.updateRigEdits(preview, config, "authoring-validation-preview");
        }
        var rig = preview.getRig();
        var parameters = new HashSet<String>();
        for (Parameter parameter : rig.getPuppet().getParameters()) parameters.add(mangledString(parameter, Parameter.class, "getId-"));
        for (JsonElement id : array(profile.get("requiredParameters"), "requiredParameters")) {
            String required = string(id, "required parameter");
            if (!parameters.contains(required)) throw new IllegalStateException("Required parameter missing: " + required);
        }
        var checkedFeet = new HashSet<String>();
        var checkedEyes = new HashSet<String>();
        for (Drawable drawable : rig.getPuppet().getDrawables()) {
            String drawableId = mangledString(drawable, Drawable.class, "getId-");
            String sourceId = rig.getLayerIdByDrawableId().get(drawableId);
            if (parents.containsKey(sourceId)) {
                if (mangledString(drawable, Drawable.class, "getParentDeformerId-") != null)
                    throw new IllegalStateException("Expected root-pinned mesh: " + drawableId);
                if (drawable.getGeometryGrid() != null && !drawable.getGeometryGrid().getAxes().isEmpty())
                    throw new IllegalStateException("Root-pinned mesh unexpectedly has geometry parameters: " + drawableId);
                checkedFeet.add(sourceId);
            }
            if (expectedEyeBindings.containsKey(sourceId)) {
                if (drawable.getGeometryGrid() == null) throw new IllegalStateException("Missing eye geometry grid: " + drawableId);
                String required = expectedEyeBindings.get(sourceId);
                boolean found = false;
                for (KeyformAxis axis : drawable.getGeometryGrid().getAxes())
                    if (required.equals(mangledString(axis, KeyformAxis.class, "getParameterId-"))) found = true;
                if (!found) throw new IllegalStateException("Eye mesh is not bound to " + required + ": " + drawableId);
                if (drawable.getGeometryGrid().getCells().stream().allMatch(cell -> allZero(cell.getForm().getPositionDeltas())))
                    throw new IllegalStateException("Eye parameter contains no geometric movement: " + drawableId);
                checkedEyes.add(sourceId);
            }
        }
        if (!checkedFeet.equals(parents.keySet()) || !checkedEyes.equals(expectedEyeBindings.keySet()))
            throw new IllegalStateException("Not every reviewed source binding survived generation");

        var result = pipeline.run(input, output, config,
            (stage, fraction) -> System.out.printf("%3d%% %s%n", Math.round(fraction * 100f), stage));
        Files.copy(profilePath, output.resolve("authoring-profile.json"));
        System.out.println("Authoring validation: root-pinned=" + checkedFeet.size() + ", moving eye bindings=" + checkedEyes.size());
        System.out.println("PSD SHA256: " + sha256(input));
        System.out.println("Profile SHA256: " + sha256(profilePath));
        Path authoringJar = Path.of(PSD2LivePipeline.class.getProtectionDomain().getCodeSource().getLocation().toURI());
        System.out.println("Authoring JAR: " + authoringJar);
        System.out.println("Authoring JAR SHA256: " + sha256(authoringJar));
        for (var file : result.getExportedFiles()) System.out.println(file.getPath() + " (" + file.getBytes() + " bytes)");
        for (String warning : result.getWarnings()) System.err.println("Warning: " + warning);
        var evidence = new StringBuilder("{\n  \"schemaVersion\": 1,\n  \"authoringAssertionsPassed\": true,")
            .append("\n  \"officialRuntimeValidation\": \"pending\",\n  \"editorRoundTripValidation\": \"pending\",")
            .append("\n  \"inputSha256\": ").append(quote(sha256(input))).append(',')
            .append("\n  \"profileSha256\": ").append(quote(sha256(profilePath))).append(',')
            .append("\n  \"authoringJarSha256\": ").append(quote(sha256(authoringJar))).append(',')
            .append("\n  \"rootPinnedMeshes\": ").append(checkedFeet.size()).append(',')
            .append("\n  \"movingEyeBindings\": ").append(checkedEyes.size()).append(',')
            .append("\n  \"drawables\": ").append(rig.getPuppet().getDrawables().size()).append(',')
            .append("\n  \"parameters\": ").append(parameters.size()).append(',')
            .append("\n  \"files\": [");
        boolean first = true;
        for (var file : result.getExportedFiles()) {
            if (!first) evidence.append(',');
            first = false;
            evidence.append("\n    {\"path\": ").append(quote(output.relativize(file.getPath()).toString().replace('\\', '/')))
                .append(", \"bytes\": ").append(file.getBytes()).append(", \"sha256\": ").append(quote(sha256(file.getPath()))).append('}');
        }
        evidence.append("\n  ],\n  \"warnings\": [");
        first = true;
        for (String warning : result.getWarnings()) {
            if (!first) evidence.append(',');
            first = false;
            evidence.append(quote(warning));
        }
        evidence.append("]\n}\n");
        Files.writeString(output.resolve("authoring-validation.json"), evidence.toString());
        System.out.println("Official Cubism runtime and Editor validation remain separate required checks");
    }

    private static PipelineConfig configure(PipelineConfig base, JsonObject settings,
            Map<String, LayerClassificationOverride> layers, Map<String, String> parents) throws Exception {
        Set<String> supported = Set.of("atlasSize", "meshSpacing", "headTurnStrength", "bodyStrength", "mouthOutlineEnabled", "generatePhysics");
        keys(settings, supported);
        Method copy = Arrays.stream(PipelineConfig.class.getMethods()).filter(method -> method.getName().equals("copy")).findFirst().orElseThrow();
        if (copy.getParameterCount() != CONFIG_COMPONENTS) throw new IllegalStateException("Unsupported PSD2Live PipelineConfig ABI");
        Object[] values = new Object[CONFIG_COMPONENTS];
        for (int i = 0; i < values.length; i++) values[i] = PipelineConfig.class.getMethod("component" + (i + 1)).invoke(base);
        if (settings.containsKey("atlasSize")) {
            int atlas = integer(settings.get("atlasSize"), "atlasSize");
            if (atlas < 256 || atlas > 4096 || (atlas & (atlas - 1)) != 0) throw new IllegalArgumentException("atlasSize must be a power of two in 256..4096");
            values[0] = atlas;
        }
        if (settings.containsKey("meshSpacing")) {
            int spacing = integer(settings.get("meshSpacing"), "meshSpacing");
            if (spacing < 8 || spacing > 128) throw new IllegalArgumentException("meshSpacing must be in 8..128");
            values[3] = spacing;
        }
        if (settings.containsKey("headTurnStrength")) values[10] = strength(settings.get("headTurnStrength"), "headTurnStrength");
        if (settings.containsKey("bodyStrength")) values[11] = strength(settings.get("bodyStrength"), "bodyStrength");
        if (settings.containsKey("mouthOutlineEnabled")) values[15] = bool(settings.get("mouthOutlineEnabled"), "mouthOutlineEnabled");
        if (settings.containsKey("generatePhysics")) values[25] = bool(settings.get("generatePhysics"), "generatePhysics");
        values[40] = layers;
        values[43] = parents;
        return (PipelineConfig) copy.invoke(base, values);
    }

    private static String mangledString(Object value, Class<?> type, String prefix) throws Exception {
        Method method = Arrays.stream(type.getMethods()).filter(candidate -> candidate.getName().startsWith(prefix) && candidate.getParameterCount() == 0).findFirst().orElseThrow();
        return (String) method.invoke(value);
    }
    private static PipelineConfig withEdits(PipelineConfig base, RigEditOverlay edits) throws Exception {
        Method copy = Arrays.stream(PipelineConfig.class.getMethods()).filter(method -> method.getName().equals("copy")).findFirst().orElseThrow();
        if (copy.getParameterCount() != CONFIG_COMPONENTS) throw new IllegalStateException("Unsupported PSD2Live PipelineConfig ABI");
        Object[] values = new Object[CONFIG_COMPONENTS];
        for (int i = 0; i < values.length; i++) values[i] = PipelineConfig.class.getMethod("component" + (i + 1)).invoke(base);
        values[45] = edits;
        return (PipelineConfig) copy.invoke(base, values);
    }
    private static boolean allZero(float[] values) {
        for (float value : values) if (Math.abs(value) > 0.0000001f) return false;
        return true;
    }
    private static JsonObject object(JsonElement value, String name) {
        if (!(value instanceof JsonObject result)) throw new IllegalArgumentException(name + " must be an object");
        return result;
    }
    private static JsonArray array(JsonElement value, String name) {
        if (!(value instanceof JsonArray result)) throw new IllegalArgumentException(name + " must be an array");
        return result;
    }
    private static String string(JsonElement value, String name) {
        if (!(value instanceof JsonPrimitive primitive) || !primitive.isString() || primitive.getContent().isBlank())
            throw new IllegalArgumentException(name + " must be a nonempty string");
        return primitive.getContent();
    }
    private static int integer(JsonElement value, String name) {
        if (!(value instanceof JsonPrimitive primitive) || primitive.isString() || !primitive.getContent().matches("[0-9]+"))
            throw new IllegalArgumentException(name + " must be a positive integer");
        return Integer.parseInt(primitive.getContent());
    }
    private static float strength(JsonElement value, String name) {
        if (!(value instanceof JsonPrimitive primitive) || primitive.isString()) throw new IllegalArgumentException(name + " must be a number");
        float result = Float.parseFloat(primitive.getContent());
        if (!Float.isFinite(result) || result <= 0 || result > 1) throw new IllegalArgumentException(name + " must be in (0,1]");
        return result;
    }
    private static boolean bool(JsonElement value, String name) {
        if (!(value instanceof JsonPrimitive primitive) || primitive.isString() || !(primitive.getContent().equals("true") || primitive.getContent().equals("false")))
            throw new IllegalArgumentException(name + " must be boolean");
        return Boolean.parseBoolean(primitive.getContent());
    }
    private static float positivePixel(JsonElement value, String name) {
        if (!(value instanceof JsonPrimitive primitive) || primitive.isString()) throw new IllegalArgumentException(name + " must be a number");
        float result = Float.parseFloat(primitive.getContent());
        if (!Float.isFinite(result) || result < 0.25f || result > 4f) throw new IllegalArgumentException(name + " must be in 0.25..4 pixels");
        return result;
    }
    private static void keys(JsonObject value, Set<String> allowed) {
        for (String key : value.keySet()) if (!allowed.contains(key)) throw new IllegalArgumentException("Unknown configuration key: " + key);
    }
    private static String sha256(Path file) throws Exception {
        return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(file)));
    }
    private static String quote(String value) {
        var result = new StringBuilder("\"");
        for (char character : value.toCharArray()) {
            if (character == '"' || character == '\\') result.append('\\').append(character);
            else if (character < 32) result.append(String.format("\\u%04x", (int) character));
            else result.append(character);
        }
        return result.append('"').toString();
    }
}
