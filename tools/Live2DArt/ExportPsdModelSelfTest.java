// SPDX-License-Identifier: GPL-3.0-or-later
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.Set;
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
        reject(input, evidence, "missing-layer", replace(validProfile, "\"name\": \"irides-l\"", "\"name\": \"absent-layer\""), "Expected exactly one source layer");
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
        if (parsedProfile.containsKey("fullBody")) {
            int actionMeshes = ((JsonObject) parsedProfile.get("fullBody")).containsKey("busyEyes") ? 14 : 10;
            if (((JsonObject) parsedProfile.get("fullBody")).containsKey("eyeAttachments")) actionMeshes++;
            if (((JsonObject) parsedProfile.get("fullBody")).containsKey("softBrows")) actionMeshes += 2;
            if (!validation.contains("\"fullBodyRig\": true") || !validation.contains("\"actionBoundMeshes\": " + actionMeshes))
                throw new AssertionError("Missing full-body mesh authoring assertions");
            JsonObject bindings = (JsonObject) Json.Default.parseToJsonElement(Files.readString(validOutput.resolve("action-bindings.json")));
            boolean pupilGaze = ((JsonArray) parsedProfile.get("layers")).stream().anyMatch(layer -> ((JsonObject) layer).containsKey("pupilGaze"));
            if (pupilGaze) {
                JsonObject gaze = (JsonObject) bindings.get("gaze");
                if (gaze == null || !gaze.toString().equals("{\"schemaVersion\":1,\"head\":true,\"eyes\":true}") ||
                    !validation.contains("\"localPupilGazeBindings\": 2"))
                    throw new AssertionError("Real local pupil bindings must declare their capability explicitly");
                mark("local pupil XY geometry, fixed ROI border, neutral and gaze-independent closed eyes verified");
                reject(input, evidence, "pupil-zero-range", changeLayer(validProfile, "irides-l", "pupilGaze", "{\"offsetXPx\":0,\"offsetYPx\":1.25}"), "pupil offsetXPx must be in");
                reject(input, evidence, "pupil-crosses-margin", changeLayer(validProfile, "irides-l", "pupilGaze", "{\"offsetXPx\":2.5,\"offsetYPx\":2}"), "Pupil offset exceeds");
                reject(input, evidence, "pupil-unknown-option", changeLayer(validProfile, "irides-l", "pupilGaze", "{\"offsetXPx\":2.5,\"offsetYPx\":1.25,\"unknown\":true}"), "Unknown configuration key");
                reject(input, evidence, "pupil-topology-change", changeLayer(validProfile, "irides-l", "mesh", "{\"maxEdgeDistance\":2,\"interiorDensity\":2}"), "pupilGaze preserves original eye topology");
            }
            if (((JsonArray) bindings.get("foregroundDrawableIds")).size() != 2)
                throw new AssertionError("Desk foreground must name exactly the two forearms");
            if (((JsonArray) bindings.get("parameters")).size() != 27)
                throw new AssertionError("Core actions require exactly 27 parameters");
            if (bindings.toString().contains("ParamHand") || bindings.toString().contains("rps-"))
                throw new AssertionError("Removed gesture parameters and meshes must not survive authoring");
            JsonObject attachments = (JsonObject) bindings.get("handAttachments");
            if (!attachments.keySet().equals(Set.of("l", "r")))
                throw new AssertionError("Both prop attachment points must survive authoring");
            if (((JsonObject) parsedProfile.get("fullBody")).containsKey("eyeAttachments")) {
                JsonObject eyes = (JsonObject) bindings.get("eyeAttachments");
                JsonObject drawables = (JsonObject) bindings.get("drawables");
                if (!eyes.keySet().equals(Set.of("l", "r")) || eyes.values().stream().anyMatch(eye ->
                    !((JsonObject) eye).get("drawableId").equals(drawables.get("face"))))
                    throw new AssertionError("Both tears must attach to the face rather than collapsing eyelids");
            }
            mark("full-body mesh movement, rest geometry, jaw shapes, two prop attachments and foreground bindings verified");
            if (((JsonObject) parsedProfile.get("fullBody")).containsKey("softBrows")) {
                if (!validation.contains("\"positiveBrowsOnly\": true"))
                    throw new AssertionError("Missing exact neutral/negative brow preservation assertion");
                mark("positive brow corners authored while neutral and negative keys stay byte-identical");
                reject(input, evidence, "brow-lift-too-large", change(validProfile, "fullBody.softBrows", "innerLiftPx", "12"), "brow innerLiftPx must be in");
                reject(input, evidence, "brow-outer-direction", change(validProfile, "fullBody.softBrows", "outerDropPx", "-2"), "brow outerDropPx must be in");
            }
            reject(input, evidence, "wing-empty-layers", change(validProfile, "fullBody.leftWing", "layers", "[]"), "Wing layers must not be empty");
            reject(input, evidence, "wing-pivot-above-root", change(validProfile, "fullBody.leftWing", "pivotY", "190"), "wing pivotY must be in");
            reject(input, evidence, "elbow-above-shoulder", change(validProfile, "fullBody.leftWing", "elbowY", "210"), "elbowY must be in");
            reject(input, evidence, "beak-gap-disabled", change(validProfile, "fullBody.beak", "gapPx", "0"), "beak gapPx must be in");
            reject(input, evidence, "beak-inner-empty", change(validProfile, "fullBody.beak", "innerBounds", "[171,172,0,18]"), "inner width must be in");
            reject(input, evidence, "beak-lower-inverted", change(validProfile, "fullBody.beak", "lowerScale", "-1"), "lowerScale must be in");
            reject(input, evidence, "foreground-missing", change(validProfile, "fullBody", "foregroundLayers", "[\"absent-part\"]"), "Foreground layer must resolve");
            reject(input, evidence, "attachment-missing", change(validProfile, "fullBody", "handAttachments", "{\"r\":{\"layer\":\"absent-part\",\"source\":[140,286]}}"), "Attachment layer is not an authored action mesh");
            reject(input, evidence, "unbound-forearm", replace(validProfile,
                "\"name\": \"forearm-r\", \"semanticTag\": \"HANDWEAR\", \"side\": \"RIGHT\", \"parentDeformer\": \"DeformBodyZBreath\"",
                "\"name\": \"forearm-r\", \"semanticTag\": \"HANDWEAR\", \"side\": \"RIGHT\", \"parentDeformer\": \"DeformHeadContainer\""),
                "Action wing must share the body coordinate frame");
            if (((JsonObject) parsedProfile.get("fullBody")).containsKey("busyEyes"))
                reject(input, evidence, "busy-eye-wrong-axis", replace(validProfile, "\"eyeParameter\": \"ParamEyeLOpen\"",
                    "\"eyeParameter\": \"ParamBreath\""), "Busy eye must bind to a real left/right eye parameter");
        }
        if (parsedProfile.containsKey("calibrationLayers")) {
            reject(input, evidence, "calibration-empty", change(validProfile, null, "calibrationLayers", "[]"), "calibrationLayers must not be empty");
            reject(input, evidence, "calibration-missing", change(validProfile, null, "calibrationLayers", "[\"absent-source\"]"), "Expected exactly one calibration layer");
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
        if (section != null) {
            String[] path = section.split("\\.", 2);
            var result = new LinkedHashMap<String, JsonElement>(root);
            result.put(path[0], Json.Default.parseToJsonElement(change(root.get(path[0]).toString(), path.length > 1 ? path[1] : null, key, value)));
            return new JsonObject(result).toString();
        }
        var object = root;
        if (!object.containsKey(key)) throw new IllegalStateException("Missing fixture property: " + key);
        var changed = new LinkedHashMap<String, JsonElement>(object);
        changed.put(key, Json.Default.parseToJsonElement(value));
        return new JsonObject(changed).toString();
    }
    private static String changeLayer(String input, String name, String key, String value) {
        JsonObject root = (JsonObject) Json.Default.parseToJsonElement(input);
        var layers = new java.util.ArrayList<JsonElement>();
        for (JsonElement element : (JsonArray) root.get("layers")) {
            JsonObject layer = (JsonObject) element;
            if (((JsonPrimitive) layer.get("name")).getContent().equals(name)) {
                var changed = new LinkedHashMap<String, JsonElement>(layer);
                changed.put(key, Json.Default.parseToJsonElement(value)); layers.add(new JsonObject(changed));
            } else layers.add(element);
        }
        var changedRoot = new LinkedHashMap<String, JsonElement>(root);
        changedRoot.put("layers", new JsonArray(layers));
        return new JsonObject(changedRoot).toString();
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
