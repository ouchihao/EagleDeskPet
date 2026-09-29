// SPDX-License-Identifier: GPL-3.0-or-later
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.LinkedHashMap;
import io.github.psd2live.core.PSD2LivePipeline;
import io.github.psd2live.core.PipelineConfig;
import kotlinx.serialization.json.*;

// This isolated authoring regression never loads desktop saves or starts the pet
public final class ExportPsdModelSelfTest {
    private static int passed;

    public static void main(String[] args) throws Exception {
        if (args.length != 3) throw new IllegalArgumentException("Expected input.psd profile.json fresh-evidence-directory");
        Path input = Path.of(args[0]).toRealPath();
        Path profile = Path.of(args[1]).toRealPath();
        Path evidence = Path.of(args[2]).toAbsolutePath().normalize();
        if (Files.exists(evidence)) throw new IllegalArgumentException("Evidence directory must not already exist");
        System.out.println("Evidence: " + evidence);
        Files.createDirectories(evidence);
        byte[] sourceBefore = digest(input);
        String validProfile = Files.readString(profile);
        JsonObject parsedProfile = (JsonObject) Json.Default.parseToJsonElement(validProfile);
        Path validOutput = evidence.resolve("valid-output");
        ExportPsdModel.main(new String[] {input.toString(), validOutput.toString(), profile.toString()});
        if (!Files.isRegularFile(validOutput.resolve("eagle.moc3")) || !Files.isRegularFile(validOutput.resolve("eagle.cmo3")))
            throw new AssertionError("Valid export lacks model outputs");
        String validation = Files.readString(validOutput.resolve("authoring-validation.json"));
        if (!validation.contains("\"rootPinnedMeshes\": 2") || !validation.contains("\"movingEyeBindings\": 2") ||
                !validation.contains("\"officialRuntimeValidation\": \"pending\""))
            throw new AssertionError("Export report does not distinguish authoring and official validation");
        mark("valid model export with separate pending official validation");
        byte[] exportedBefore = digest(validOutput.resolve("eagle.moc3"));
        try {
            ExportPsdModel.main(new String[] {input.toString(), validOutput.toString(), profile.toString()});
            throw new AssertionError("Existing output was not rejected");
        } catch (IllegalArgumentException exception) {
            if (!exception.getMessage().contains("Refusing to overwrite")) throw exception;
        }
        if (!Arrays.equals(exportedBefore, digest(validOutput.resolve("eagle.moc3")))) throw new AssertionError("Existing model changed");
        mark("existing output rejected and model hash unchanged");

        int sourceLayers = Integer.parseInt(((JsonPrimitive) parsedProfile.get("expectedSourceLayers")).getContent());
        reject(input, evidence, "source-count", change(validProfile, null, "expectedSourceLayers", Integer.toString(sourceLayers + 1)), "Source layer count differs");
        reject(input, evidence, "unknown-setting", rename(validProfile, "config", "meshSpacing", "notASetting"), "Unknown configuration key");
        reject(input, evidence, "missing-layer", replace(validProfile, "\"irides-l\"", "\"absent-layer\""), "Expected exactly one source layer");
        reject(input, evidence, "invalid-atlas", change(validProfile, "config", "atlasSize", "777"), "atlasSize must be a power of two");
        reject(input, evidence, "wrong-eye-parameter", replace(validProfile, "\"requiredGeometryParameter\": \"ParamEyeLOpen\"", "\"requiredGeometryParameter\": \"ParamBreath\""), "Eye mesh is not bound");
        reject(input, evidence, "unsupported-parent", replace(validProfile, "\"name\": \"footwear-l\", \"parent\": null", "\"name\": \"footwear-l\", \"parent\": \"../../escape\""), "Only explicit null/root parent");
        if (parsedProfile.containsKey("headConnection")) {
            if (!validation.contains("\"chestAnchoredHead\": true")) throw new AssertionError("Missing chest authoring assertion");
            mark("chest head authoring assertions passed without claiming official verification");
            var source = new PSD2LivePipeline().inspect(input, new PipelineConfig()).getSource();
            JsonObject connection = (JsonObject) parsedProfile.get("headConnection");
            float startY = Float.parseFloat(((JsonPrimitive) connection.get("softAnchorStartY")).getContent());
            reject(input, evidence, "head-pivot-outside-canvas", change(validProfile, "headConnection", "pivotX", Integer.toString(source.getWidthPx() + 1)), "pivotX must be in");
            reject(input, evidence, "head-anchor-inverted", change(validProfile, "headConnection", "softAnchorEndY", Float.toString(startY - 1f)), "increasing soft-anchor band");
            reject(input, evidence, "head-anchor-beyond-head", change(validProfile, "headConnection", "softAnchorEndY", Integer.toString(source.getHeightPx() - 1)), "at least one fully anchored head control point");
            reject(input, evidence, "head-pivot-outside-band", change(validProfile, "headConnection", "pivotY", Float.toString(startY - 1f)), "increasing soft-anchor band");
            reject(input, evidence, "head-movement-disabled", change(validProfile, "headConnection", "maxTiltDegrees", "0"), "maxTiltDegrees must be in");
            reject(input, evidence, "unknown-head-setting", rename(validProfile, "headConnection", "pivotX", "unexpectedPivot"), "Unknown configuration key");
            if (connection.containsKey("pinFaceFeathers")) {
                if (!validation.contains("\"featherSurfaceAnchored\": true")) throw new AssertionError("Missing feather surface assertion");
                mark("local feather anchoring preserves neutral and upper facial controls");
                reject(input, evidence, "invalid-feather-anchor-flag", change(validProfile, "headConnection", "pinFaceFeathers", "\"true\""), "pinFaceFeathers must be boolean");
            }
        }
        if (!Arrays.equals(sourceBefore, digest(input))) throw new AssertionError("Authoring source was modified");
        mark("input PSD hash unchanged across valid and rejected exports");
        System.out.println("ExportPsdModelSelfTest: " + passed + "/" + passed + " passed");
    }

    private static void reject(Path input, Path evidence, String name, String profileJson, String expected) throws Exception {
        Path profile = evidence.resolve(name + ".json");
        Path output = evidence.resolve(name + "-output");
        Files.writeString(profile, profileJson);
        try {
            ExportPsdModel.main(new String[] {input.toString(), output.toString(), profile.toString()});
            throw new AssertionError(name + " was not rejected");
        } catch (IllegalArgumentException | IllegalStateException exception) {
            if (!exception.getMessage().contains(expected)) throw exception;
        }
        if (Files.exists(output)) throw new AssertionError(name + " created output before validation");
        mark(name + " rejected before output creation");
    }
    private static String replace(String input, String expected, String replacement) {
        int first = input.indexOf(expected);
        if (first < 0 || input.indexOf(expected, first + expected.length()) >= 0)
            throw new IllegalStateException("Fixture fragment must match exactly once: " + expected);
        return input.substring(0, first) + replacement + input.substring(first + expected.length());
    }
    private static String change(String input, String section, String key, String value) {
        JsonObject root = (JsonObject) Json.Default.parseToJsonElement(input);
        var object = section == null ? root : (JsonObject) root.get(section);
        if (!object.containsKey(key)) throw new IllegalStateException("Missing fixture property: " + key);
        var changed = new LinkedHashMap<String, JsonElement>(object);
        changed.put(key, Json.Default.parseToJsonElement(value));
        if (section == null) return new JsonObject(changed).toString();
        var result = new LinkedHashMap<String, JsonElement>(root);
        result.put(section, new JsonObject(changed));
        return new JsonObject(result).toString();
    }
    private static String rename(String input, String section, String oldKey, String newKey) {
        JsonObject root = (JsonObject) Json.Default.parseToJsonElement(input);
        var changed = new LinkedHashMap<String, JsonElement>((JsonObject) root.get(section));
        if (!changed.containsKey(oldKey) || changed.containsKey(newKey)) throw new IllegalStateException("Invalid fixture property rename");
        changed.put(newKey, changed.remove(oldKey));
        var result = new LinkedHashMap<String, JsonElement>(root);
        result.put(section, new JsonObject(changed));
        return new JsonObject(result).toString();
    }
    private static byte[] digest(Path path) throws Exception {
        return MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(path));
    }
    private static void mark(String description) {
        passed++;
        System.out.println("PASS " + description);
    }
}
