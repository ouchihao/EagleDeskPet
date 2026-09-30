// SPDX-License-Identifier: GPL-3.0-or-later
import io.github.psd2live.core.*;
import kotlinx.serialization.json.*;
import org.umamo.format.art.SourceLayer;
import org.umamo.runtime.model.Drawable;
import org.umamo.runtime.model.Deformer;
import org.umamo.runtime.model.KeyformAxis;
import org.umamo.runtime.model.KeyformGrid;
import org.umamo.runtime.model.Parameter;
import org.umamo.runtime.model.ParameterKind;
import org.umamo.runtime.model.FormChannel;
import org.umamo.runtime.model.ChannelValue;
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
        keys(profile, Set.of("schemaVersion", "psd2liveVersion", "expectedSourceLayers", "config", "layers", "requiredParameters", "headConnection", "fullBody", "calibrationLayers"));
        if (integer(profile.get("schemaVersion"), "schemaVersion") != 1 || !string(profile.get("psd2liveVersion"), "psd2liveVersion").equals("1.1.1"))
            throw new IllegalArgumentException("This adapter requires schemaVersion 1 and PSD2Live 1.1.1");
        var pipeline = new PSD2LivePipeline();
        var base = new PipelineConfig();
        var analysis = pipeline.inspect(input, base);
        if (analysis.getSource().getLayers().size() != integer(profile.get("expectedSourceLayers"), "expectedSourceLayers"))
            throw new IllegalArgumentException("Source layer count differs from reviewed profile");

        var overrides = new LinkedHashMap<String, LayerClassificationOverride>();
        var meshes = new LinkedHashMap<String, MeshSettings>();
        var parents = new LinkedHashMap<String, String>();
        var rootPinned = new HashSet<String>();
        var expectedEyeBindings = new LinkedHashMap<String, String>();
        var eyeClosures = new LinkedHashMap<String, JsonObject>();
        var pupilGazes = new LinkedHashMap<String, JsonObject>();
        var names = new HashSet<String>();
        for (JsonElement item : array(profile.get("layers"), "layers")) {
            JsonObject rule = object(item, "layer rule");
            keys(rule, Set.of("name", "semanticTag", "side", "parent", "parentDeformer", "requiredGeometryParameter", "closedEye", "pupilGaze", "mesh"));
            String name = string(rule.get("name"), "layer name");
            if (!names.add(name)) throw new IllegalArgumentException("Duplicate profile layer: " + name);
            var matches = analysis.getSource().getLayers().stream().filter(layer -> layer.getName().equals(name)).toList();
            if (matches.size() != 1) throw new IllegalArgumentException("Expected exactly one source layer: " + name);
            SourceLayer source = matches.getFirst();
            String sourceId = mangledString(source, SourceLayer.class, "getId-");
            if (rule.containsKey("mesh")) {
                JsonObject mesh = object(rule.get("mesh"), "mesh");
                keys(mesh, Set.of("maxEdgeDistance", "interiorDensity"));
                meshes.put(sourceId, new MeshSettings(1f, false, 10f,
                    boundedNumber(mesh.get("maxEdgeDistance"), "maxEdgeDistance", 1, 32),
                    boundedNumber(mesh.get("interiorDensity"), "interiorDensity", 2, 64)));
            }
            if (rule.containsKey("semanticTag")) {
                SemanticTag tag = SemanticTag.valueOf(string(rule.get("semanticTag"), "semanticTag"));
                Side side = Side.valueOf(string(rule.get("side"), "side"));
                overrides.put(sourceId, new LayerClassificationOverride(tag, side));
            } else if (rule.containsKey("side")) throw new IllegalArgumentException("side requires semanticTag");
            if (rule.containsKey("parent")) {
                if (rule.get("parent") != JsonNull.INSTANCE) throw new IllegalArgumentException("Only explicit null/root parent is supported");
                parents.put(sourceId, null);
                rootPinned.add(sourceId);
            }
            if (rule.containsKey("parentDeformer")) {
                if (rule.containsKey("parent")) throw new IllegalArgumentException("parent and parentDeformer are mutually exclusive");
                String parent = string(rule.get("parentDeformer"), "parentDeformer");
                if (!Set.of("DeformHeadContainer", "DeformFaceNinePose", "DeformBodyZBreath").contains(parent))
                    throw new IllegalArgumentException("Unsupported explicit parent deformer: " + parent);
                parents.put(sourceId, parent);
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
            if (rule.containsKey("pupilGaze")) {
                if (!rule.containsKey("closedEye")) throw new IllegalArgumentException("pupilGaze requires closedEye");
                if (rule.containsKey("mesh")) throw new IllegalArgumentException("pupilGaze preserves original eye topology");
                JsonObject gaze = object(rule.get("pupilGaze"), "pupilGaze");
                keys(gaze, Set.of("offsetXPx", "offsetYPx"));
                boundedNumber(gaze.get("offsetXPx"), "pupil offsetXPx", 0.25f, 3);
                boundedNumber(gaze.get("offsetYPx"), "pupil offsetYPx", 0.25f, 2);
                pupilGazes.put(sourceId, gaze);
            }
            System.out.println("Layer rule: " + name + " => " + sourceId + " " + rule);
        }

        PipelineConfig config = configure(base, object(profile.get("config"), "config"), overrides, parents, meshes);
        RigEditOverlay edits = new RigEditOverlay();
        if (profile.containsKey("calibrationLayers")) {
            var calibration = new LinkedHashSet<String>();
            for (JsonElement item : array(profile.get("calibrationLayers"), "calibrationLayers")) {
                String name = string(item, "calibration layer");
                var matches = analysis.getSource().getLayers().stream().filter(layer -> layer.getName().equals(name)).toList();
                if (matches.size() != 1) throw new IllegalArgumentException("Expected exactly one calibration layer: " + name);
                if (!calibration.add(mangledString(matches.getFirst(), SourceLayer.class, "getId-")))
                    throw new IllegalArgumentException("Duplicate calibration layer: " + name);
            }
            if (calibration.isEmpty()) throw new IllegalArgumentException("calibrationLayers must not be empty");
            edits = edits.copy(edits.getParameterEdits(), edits.getDeletedParameterIds(), edits.getKeyformSetEdits(),
                edits.getKeyformDeleteEdits(), edits.getKeyformCopyEdits(), edits.getWarpEdits(), edits.getPhysicsEdits(),
                edits.getAssetLayers(), calibration, edits.getStructureEdits(), edits.getAuthoringJournal());
            config = withEdits(config, edits);
        }
        var preview = pipeline.buildPreview(input, config);
        if (!eyeClosures.isEmpty()) {
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
                JsonObject gaze = pupilGazes.get(sourceId);
                float[] pupil = gaze == null ? null : pupilBounds(source);
                float[] gazeKeys = gaze == null ? new float[] {0} : new float[] {-1, 0, 1};
                float offsetX = gaze == null ? 0 : boundedNumber(gaze.get("offsetXPx"), "pupil offsetXPx", 0.25f, 3);
                float offsetY = gaze == null ? 0 : boundedNumber(gaze.get("offsetYPx"), "pupil offsetYPx", 0.25f, 2);
                if (gaze != null && (offsetX > Math.min(pupil[0], source.getRaster().getWidth() - pupil[2]) * 0.5f ||
                    offsetY > Math.min(pupil[1], sourceHeight - pupil[3]) * 0.5f))
                    throw new IllegalArgumentException("Pupil offset exceeds the fixed face-color margin");
                int pupilVertices = 0;
                for (int i = 0; gaze != null && i < positions.length; i += 2)
                    if (pupilWeight((positions[i] - minX) / (maxX - minX) * source.getRaster().getWidth(),
                        (positions[i + 1] - minY) / (maxY - minY) * sourceHeight, source, pupil) > 0.25f) pupilVertices++;
                if (gaze != null && pupilVertices < 1) throw new IllegalStateException("Pupil gaze requires an interior pupil vertex");
                for (float openness : new float[] {0f, 0.5f, 1f}) for (float gazeX : gazeKeys) for (float gazeY : gazeKeys) {
                    var delta = new ArrayList<Float>(positions.length);
                    for (int i = 0; i < positions.length; i += 2) {
                        float u = 2f * (positions[i] - minX) / (maxX - minX) - 1f;
                        float closeDelta = centerY + curve * (1f - u * u) +
                            (positions[i + 1] - centerY) * closeScale - positions[i + 1];
                        float weight = gaze == null ? 0 : pupilWeight((positions[i] - minX) / (maxX - minX) * source.getRaster().getWidth(),
                            (positions[i + 1] - minY) / (maxY - minY) * sourceHeight, source, pupil) * openness;
                        delta.add(weight == 0 || gazeX == 0 ? 0f : gazeX * offsetX * weight * (maxX - minX) / source.getRaster().getWidth());
                        delta.add(closeDelta * (1f - openness) - gazeY * offsetY * weight * (maxY - minY) / sourceHeight);
                    }
                    var pose = new LinkedHashMap<String, Float>();
                    pose.put(parameter, openness);
                    if (gaze != null) { pose.put("ParamEyeBallX", gazeX); pose.put("ParamEyeBallY", gazeY); }
                    edits = edits.setKeyform(new RigKeyformSetEdit(target, pose,
                        new RigKeyformGeometryEdit(null, null, null, null, null, delta), null));
                }
                System.out.println("Authored eye closure: " + drawableId + " -> " + parameter + " @ 0 / 0.5 / 1, no opacity fade");
                if (gaze != null) System.out.println("Authored local pupil gaze: " + drawableId + " 27 joint keys, pinned white ROI boundary, pupil vertices=" + pupilVertices);
            }
        }
        if (profile.containsKey("headConnection"))
            edits = authorHeadConnection(edits, preview.getRig().getPuppet().getDeformers(),
                preview.getAnalysis().getAnchors().getCharacter(),
                object(profile.get("headConnection"), "headConnection"),
                analysis.getSource().getWidthPx(), analysis.getSource().getHeightPx());
        var actionBindings = new LinkedHashMap<String, String>();
        var unmodifiedRig = preview.getRig();
        if (profile.containsKey("fullBody"))
            edits = authorFullBody(edits, preview.getRig(), preview.getAnalysis(),
                object(profile.get("fullBody"), "fullBody"), actionBindings);
        config = withEdits(config, edits);
        preview = pipeline.updateRigEdits(preview, config, "authoring-validation-preview");
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
            if (rootPinned.contains(sourceId)) {
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
                if (pupilGazes.containsKey(sourceId)) validatePupilGaze(drawable, required);
            }
        }
        if (!checkedFeet.equals(rootPinned) || !checkedEyes.equals(expectedEyeBindings.keySet()))
            throw new IllegalStateException("Not every reviewed source binding survived generation");
        if (profile.containsKey("fullBody")) validateFullBody(rig, unmodifiedRig, object(profile.get("fullBody"), "fullBody"), actionBindings);
        if (!pupilGazes.isEmpty() && (pupilGazes.size() != 2 || !profile.containsKey("headConnection")))
            throw new IllegalStateException("Gaze capability requires both pupils and the authored head connection");

        var result = pipeline.run(input, output, config,
            (stage, fraction) -> System.out.printf("%3d%% %s%n", Math.round(fraction * 100f), stage));
        Files.copy(profilePath, output.resolve("authoring-profile.json"));
        var mapping = new StringBuilder("{\n  \"schemaVersion\": 1,\n  \"drawables\": {");
        boolean firstMapping = true;
        for (var entry : actionBindings.entrySet()) {
            if (!firstMapping) mapping.append(',');
            firstMapping = false;
            mapping.append("\n    ").append(quote(entry.getKey())).append(": ").append(quote(entry.getValue()));
        }
        mapping.append("\n  },\n  \"foregroundDrawableIds\": [");
        firstMapping = true;
        if (profile.containsKey("fullBody")) {
            for (JsonElement item : array(object(profile.get("fullBody"), "fullBody").get("foregroundLayers"), "foregroundLayers")) {
                if (!firstMapping) mapping.append(',');
                firstMapping = false;
                mapping.append(quote(actionBindings.get(string(item, "foreground layer"))));
            }
        }
        mapping.append(']');
        if (!pupilGazes.isEmpty()) {
            mapping.append(",\n  \"gaze\": {\"schemaVersion\": 1, \"head\": true, \"eyes\": true}");
        }
        if (profile.containsKey("fullBody")) {
            JsonObject fullBody = object(profile.get("fullBody"), "fullBody");
            for (String group : List.of("handAttachments", "eyeAttachments")) {
                if (!fullBody.containsKey(group)) continue;
                JsonObject attachments = object(fullBody.get(group), group);
                mapping.append(",\n  ").append(quote(group)).append(": {");
                boolean firstAttachment = true;
                for (var entry : attachments.entrySet()) {
                    JsonObject attachment = object(entry.getValue(), "attachment");
                    if (!firstAttachment) mapping.append(',');
                    firstAttachment = false;
                    mapping.append("\n    ").append(quote(entry.getKey())).append(": {\"drawableId\": ")
                        .append(quote(actionBindings.get(string(attachment.get("layer"), "attachment layer"))))
                        .append(", \"source\": ").append(attachment.get("source")).append('}');
                }
                mapping.append("\n  }");
            }
        }
        mapping.append(",\n  \"parameters\": [");
        firstMapping = true;
        for (Parameter parameter : rig.getPuppet().getParameters()) {
            if (!firstMapping) mapping.append(',');
            firstMapping = false;
            mapping.append("\n    {\"id\": ").append(quote(mangledString(parameter, Parameter.class, "getId-")))
                .append(", \"min\": ").append(parameter.getMin()).append(", \"max\": ").append(parameter.getMax())
                .append(", \"default\": ").append(parameter.getDefault()).append('}');
        }
        mapping.append("\n  ]\n}\n");
        if (!actionBindings.isEmpty()) Files.writeString(output.resolve("action-bindings.json"), mapping.toString());
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
            .append("\n  \"localPupilGazeBindings\": ").append(pupilGazes.size()).append(',')
            .append("\n  \"chestAnchoredHead\": ").append(profile.containsKey("headConnection")).append(',')
            .append("\n  \"fullBodyRig\": ").append(profile.containsKey("fullBody")).append(',')
            .append("\n  \"actionBoundMeshes\": ").append(profile.containsKey("fullBody") ? actionBindings.size() : 0).append(',')
            .append("\n  \"positiveBrowsOnly\": ").append(profile.containsKey("fullBody") && object(profile.get("fullBody"), "fullBody").containsKey("softBrows")).append(',')
            .append("\n  \"featherSurfaceAnchored\": ").append(profile.containsKey("headConnection") &&
                object(profile.get("headConnection"), "headConnection").containsKey("pinFaceFeathers") &&
                bool(object(profile.get("headConnection"), "headConnection").get("pinFaceFeathers"), "pinFaceFeathers")).append(',')
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

    private static float[] pupilBounds(SourceLayer source) {
        var raster = source.getRaster();
        byte[] rgba = raster.getRgba();
        int left = raster.getWidth(), top = raster.getHeight(), right = 0, bottom = 0, count = 0;
        for (int y = 0; y < raster.getHeight(); y++) for (int x = 0; x < raster.getWidth(); x++) {
            int offset = (y * raster.getWidth() + x) * 4;
            if ((rgba[offset + 3] & 255) < 128 || Math.max(rgba[offset] & 255,
                Math.max(rgba[offset + 1] & 255, rgba[offset + 2] & 255)) >= 180) continue;
            left = Math.min(left, x); top = Math.min(top, y);
            right = Math.max(right, x + 1); bottom = Math.max(bottom, y + 1); count++;
        }
        if (count < 12 || left < 2 || top < 2 || right > raster.getWidth() - 2 || bottom > raster.getHeight() - 2)
            throw new IllegalStateException("Pupil gaze requires a dark pupil surrounded by a fixed face-color margin");
        return new float[] {left, top, right, bottom};
    }

    private static float pupilWeight(float x, float y, SourceLayer source, float[] pupil) {
        float xWeight = x < pupil[0] ? smoothStep(x / pupil[0]) : x > pupil[2]
            ? smoothStep((source.getRaster().getWidth() - x) / (source.getRaster().getWidth() - pupil[2])) : 1;
        float yWeight = y < pupil[1] ? smoothStep(y / pupil[1]) : y > pupil[3]
            ? smoothStep((source.getRaster().getHeight() - y) / (source.getRaster().getHeight() - pupil[3])) : 1;
        return xWeight * yWeight;
    }

    private static void validatePupilGaze(Drawable drawable, String eyeParameter) throws Exception {
        var grid = drawable.getGeometryGrid();
        requireAxes(grid, Set.of(eyeParameter, "ParamEyeBallX", "ParamEyeBallY"));
        if (grid.getCells().size() != 27) throw new IllegalStateException("Pupil gaze requires 27 joint eyelid/XY keys");
        float[] closed = null;
        boolean movedX = false, movedY = false;
        float[] positions = drawable.getMesh().getPositions();
        float minX = Float.POSITIVE_INFINITY, minY = Float.POSITIVE_INFINITY, maxX = Float.NEGATIVE_INFINITY, maxY = Float.NEGATIVE_INFINITY;
        for (int i = 0; i < positions.length; i += 2) {
            minX = Math.min(minX, positions[i]); maxX = Math.max(maxX, positions[i]);
            minY = Math.min(minY, positions[i + 1]); maxY = Math.max(maxY, positions[i + 1]);
        }
        for (var cell : grid.getCells()) {
            var pose = coordinate(grid, cell.getCoordinate());
            float[] deltas = cell.getForm().getPositionDeltas();
            if (pose.get(eyeParameter) == 0) {
                if (closed == null) closed = deltas;
                else if (!Arrays.equals(closed, deltas)) throw new IllegalStateException("Closed eyelids must not follow pupil gaze");
            }
            if (pose.get(eyeParameter) == 1) {
                if (pose.get("ParamEyeBallX") == 0 && pose.get("ParamEyeBallY") == 0 && !allZero(deltas))
                    throw new IllegalStateException("Pupil gaze changed the neutral eye geometry");
                for (int i = 0; i < deltas.length; i += 2) {
                    if ((positions[i] == minX || positions[i] == maxX || positions[i + 1] == minY || positions[i + 1] == maxY) &&
                        (deltas[i] != 0 || deltas[i + 1] != 0))
                        throw new IllegalStateException("Pupil gaze moved a white ROI boundary vertex");
                    movedX |= Math.abs(deltas[i]) > 0.00001f;
                    movedY |= Math.abs(deltas[i + 1]) > 0.00001f;
                }
            }
        }
        if (!movedX || !movedY) throw new IllegalStateException("Pupil gaze contains an inert axis");
        System.out.println("Pupil gaze verification: real XY geometry, exact neutral, identical closed eyelids across all gaze keys");
    }

    private static PipelineConfig configure(PipelineConfig base, JsonObject settings,
            Map<String, LayerClassificationOverride> layers, Map<String, String> parents, Map<String, MeshSettings> meshes) throws Exception {
        Set<String> supported = Set.of("atlasSize", "meshSpacing", "headTurnStrength", "bodyStrength", "featureDisplacementEnabled", "mouthOutlineEnabled", "generatePhysics");
        keys(settings, supported);
        Method copy = Arrays.stream(PipelineConfig.class.getMethods()).filter(method -> method.getName().equals("copy")).findFirst().orElseThrow();
        if (copy.getParameterCount() != CONFIG_COMPONENTS) throw new IllegalStateException("Unsupported PSD2Live PipelineConfig ABI");
        Object[] values = new Object[CONFIG_COMPONENTS];
        for (int i = 0; i < values.length; i++) values[i] = PipelineConfig.class.getMethod("component" + (i + 1)).invoke(base);
        values[8] = meshes;
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
        if (settings.containsKey("featureDisplacementEnabled")) values[14] = bool(settings.get("featureDisplacementEnabled"), "featureDisplacementEnabled");
        if (settings.containsKey("mouthOutlineEnabled")) values[15] = bool(settings.get("mouthOutlineEnabled"), "mouthOutlineEnabled");
        if (settings.containsKey("generatePhysics")) values[25] = bool(settings.get("generatePhysics"), "generatePhysics");
        values[40] = layers;
        values[43] = parents;
        return (PipelineConfig) copy.invoke(base, values);
    }

    private record MeshSpace(Drawable drawable, String id, float[] positions, float minX, float minY,
            float scaleX, float scaleY, Bounds canvas) {
        float x(int vertex) { return canvas.getLeft() + (positions[vertex] - minX) * scaleX; }
        float y(int vertex) { return canvas.getTop() + (positions[vertex + 1] - minY) * scaleY; }
    }

    private static RigEditOverlay authorFullBody(RigEditOverlay edits, BuiltRig rig, PipelineAnalysis analysis,
            JsonObject settings, Map<String, String> bindings) throws Exception {
        keys(settings, Set.of("leftWing", "rightWing", "wingRootY", "bodyGroundY", "beak", "busyEyes", "foregroundLayers", "handAttachments", "eyeAttachments", "softBrows"));
        int width = analysis.getSource().getWidthPx(), height = analysis.getSource().getHeightPx();
        float rootY = boundedNumber(settings.get("wingRootY"), "wingRootY", 0, height);
        float groundY = boundedNumber(settings.get("bodyGroundY"), "bodyGroundY", 1, height);
        if (settings.containsKey("eyeAttachments"))
            for (var item : object(settings.get("eyeAttachments"), "eyeAttachments").values())
                meshSpace(string(object(item, "eye attachment").get("layer"), "eye attachment layer"), rig, analysis, bindings);
        if (settings.containsKey("softBrows")) {
            JsonObject brows = object(settings.get("softBrows"), "softBrows");
            keys(brows, Set.of("left", "right", "innerLiftPx", "outerDropPx"));
            float lift = boundedNumber(brows.get("innerLiftPx"), "brow innerLiftPx", 1, 6);
            float drop = boundedNumber(brows.get("outerDropPx"), "brow outerDropPx", 0, 1);
            for (String side : List.of("L", "R")) {
                MeshSpace mesh = meshSpace(string(brows.get(side.equals("L") ? "left" : "right"), "brow layer"), rig, analysis, bindings);
                var delta = new ArrayList<Float>(mesh.positions().length);
                for (int i = 0; i < mesh.positions().length; i += 2) {
                    float u = (mesh.x(i) - mesh.canvas().getLeft()) / mesh.canvas().getWidth();
                    float inner = smoothStep(side.equals("L") ? 1 - u : u);
                    delta.add(0f); delta.add((drop * (1 - inner) - lift * inner) / mesh.scaleY());
                }
                edits = edits.setKeyform(new RigKeyformSetEdit(new RigTargetRef(RigTargetKind.ART_MESH, mesh.id(), null),
                    Map.of("ParamBrow" + side + "Y", 1f), new RigKeyformGeometryEdit(null, null, null, null, null, delta), null));
            }
        }
        for (String side : List.of("L", "R")) {
            JsonObject wing = object(settings.get(side.equals("L") ? "leftWing" : "rightWing"), "wing");
            keys(wing, Set.of("layers", "pivotX", "pivotY", "elbowX", "elbowY", "foldDegrees"));
            float pivotX = boundedNumber(wing.get("pivotX"), "wing pivotX", 0, width);
            float pivotY = boundedNumber(wing.get("pivotY"), "wing pivotY", rootY + 1, height);
            float elbowX = boundedNumber(wing.get("elbowX"), "elbowX", 0, width);
            float elbowY = boundedNumber(wing.get("elbowY"), "elbowY", pivotY + 4, height);
            float foldDegrees = boundedNumber(wing.get("foldDegrees"), "foldDegrees", 10, 130);
            for (String suffix : List.of("Lift", "Swing", "Fold"))
                edits = parameter(edits, "ParamWing" + side + suffix, suffix.equals("Swing") ? -1 : 0, 1);
            for (JsonElement item : array(wing.get("layers"), "wing layers")) {
                String layer = string(item, "wing layer");
                MeshSpace mesh = meshSpace(layer, rig, analysis, bindings);
                edits = wingKeys(edits, mesh, side, pivotX, pivotY, rootY, elbowX, elbowY, foldDegrees);
            }
        }
        edits = parameter(edits, "ParamBodySquash", -1, 1);
        Deformer.Warp body = null;
        for (Deformer deformer : rig.getPuppet().getDeformers())
            if (deformer instanceof Deformer.Warp warp && "DeformBodyXY".equals(mangledString(deformer, Deformer.class, "getId-"))) body = warp;
        if (body == null || body.getGeometryGrid() == null) throw new IllegalStateException("Missing body warp");
        var bodyGrid = body.getGeometryGrid();
        float centerX = analysis.getAnchors().getCharacter().getCenterX();
        for (float squash : new float[] {-1, -0.5f, 0, 0.5f, 1}) for (var cell : bodyGrid.getCells()) {
            var points = new ArrayList<Float>();
            float[] current = cell.getForm().getControlPoints();
            for (int i = 0; i < current.length; i += 2) {
                points.add(squash == 0 ? current[i] : centerX + (current[i] - centerX) * (1 + 0.055f * squash));
                points.add(squash == 0 ? current[i + 1] : groundY + (current[i + 1] - groundY) * (1 - (squash > 0 ? 0.08f : 0.06f) * squash));
            }
            var pose = coordinate(bodyGrid, cell.getCoordinate()); pose.put("ParamBodySquash", squash);
            edits = edits.setKeyform(new RigKeyformSetEdit(new RigTargetRef(RigTargetKind.WARP_DEFORMER, "DeformBodyXY", null), pose,
                new RigKeyformGeometryEdit(points, null, null, null, null, null), null));
        }
        if (settings.containsKey("beak")) edits = authorBeak(edits, rig, analysis, object(settings.get("beak"), "beak"), bindings);
        if (settings.containsKey("busyEyes")) {
            edits = parameter(edits, "ParamBusyEyes", 0, 1);
            for (JsonElement item : array(settings.get("busyEyes"), "busyEyes")) {
                JsonObject eye = object(item, "busy eye");
                keys(eye, Set.of("layer", "normal", "eyeParameter", "heightPx", "curveDepthPx"));
                MeshSpace busy = meshSpace(string(eye.get("layer"), "busy eye layer"), rig, analysis, bindings);
                MeshSpace normal = meshSpace(string(eye.get("normal"), "normal eye layer"), rig, analysis, bindings);
                String parameter = string(eye.get("eyeParameter"), "eyeParameter");
                if (!Set.of("ParamEyeLOpen", "ParamEyeROpen").contains(parameter))
                    throw new IllegalArgumentException("Busy eye must bind to a real left/right eye parameter");
                float heightPx = positivePixel(eye.get("heightPx"), "heightPx");
                float curve = positivePixel(eye.get("curveDepthPx"), "curveDepthPx");
                for (float open : new float[] {0, 0.5f, 1}) {
                    var delta = new ArrayList<Float>(busy.positions().length);
                    for (int i = 0; i < busy.positions().length; i += 2) {
                        float x = busy.x(i), y = busy.y(i);
                        float u = 2f * (x - busy.canvas().getLeft()) / busy.canvas().getWidth() - 1f;
                        float closedY = busy.canvas().getCenterY() + curve * (1f - u * u) +
                            (y - busy.canvas().getCenterY()) * heightPx / busy.canvas().getHeight();
                        delta.add(0f); delta.add((closedY - y) * (1 - open) / busy.scaleY());
                    }
                    edits = edits.setKeyform(new RigKeyformSetEdit(new RigTargetRef(RigTargetKind.ART_MESH, busy.id(), null),
                        Map.of(parameter, open), new RigKeyformGeometryEdit(null, null, null, null, null, delta), null));
                }
                for (float key : new float[] {0, 1}) {
                    edits = opacity(edits, busy.id(), "ParamBusyEyes", key, key);
                    edits = opacity(edits, normal.id(), "ParamBusyEyes", key, 1 - key);
                }
            }
            System.out.println("Authored busy eyes: independent yellow eye rasters, geometric eyelid closure, blink-masked binary selection");
        }
        System.out.println("Authored full-body motion: independent wing Lift/Swing/Fold, five body squash keys, fixed foot meshes retained");
        return edits;
    }

    private static RigEditOverlay parameter(RigEditOverlay edits, String id, float min, float max) {
        return edits.upsert(new RigParameterEdit(id, id, min, max, 0f, ParameterKind.NORMAL, false, true));
    }

    private static void validateFullBody(BuiltRig rig, BuiltRig unmodifiedRig, JsonObject settings, Map<String, String> bindings) throws Exception {
        var meshes = new HashMap<String, Drawable>();
        for (Drawable drawable : rig.getPuppet().getDrawables())
            meshes.put(mangledString(drawable, Drawable.class, "getId-"), drawable);
        if (settings.containsKey("softBrows")) {
            JsonObject brows = object(settings.get("softBrows"), "softBrows");
            for (String side : List.of("L", "R")) {
                String id = bindings.get(string(brows.get(side.equals("L") ? "left" : "right"), "brow layer"));
                Drawable before = null;
                for (Drawable drawable : unmodifiedRig.getPuppet().getDrawables())
                    if (id.equals(mangledString(drawable, Drawable.class, "getId-"))) before = drawable;
                var grid = meshes.get(id).getGeometryGrid();
                if (before == null || grid == null || grid.getCells().size() != 3)
                    throw new IllegalStateException("Soft brow must retain its three native keys");
                requireAxes(grid, Set.of("ParamBrow" + side + "Y"));
                if (!Arrays.equals(before.getMesh().getPositions(), meshes.get(id).getMesh().getPositions()))
                    throw new IllegalStateException("Soft brow changed its neutral mesh");
                var originalGrid = before.getGeometryGrid();
                for (var cell : grid.getCells()) {
                    float key = grid.getAxes().getFirst().getKeys()[cell.getCoordinate()[0]];
                    var original = originalGrid.getCells().stream().filter(candidate ->
                        originalGrid.getAxes().getFirst().getKeys()[candidate.getCoordinate()[0]] == key).findFirst().orElseThrow();
                    boolean same = Arrays.equals(original.getForm().getPositionDeltas(), cell.getForm().getPositionDeltas());
                    if ((key <= 0) != same) throw new IllegalStateException("Soft brow must change only the positive key");
                }
            }
            System.out.println("Soft brow verification: both neutral and negative keys preserved exactly; only positive inner corners lifted");
        }
        var roles = new LinkedHashMap<String, String>();
        for (String side : List.of("L", "R")) {
            JsonObject wing = object(settings.get(side.equals("L") ? "leftWing" : "rightWing"), "wing");
            if (array(wing.get("layers"), "wing layers").isEmpty()) throw new IllegalArgumentException("Wing layers must not be empty");
            for (JsonElement layer : array(wing.get("layers"), "wing layers")) roles.put(string(layer, "wing layer"), side);
        }
        var foreground = new HashSet<String>();
        for (JsonElement item : array(settings.get("foregroundLayers"), "foregroundLayers")) {
            String layer = string(item, "foreground layer");
            if (!bindings.containsKey(layer) || !foreground.add(layer))
                throw new IllegalArgumentException("Foreground layer must resolve to one unique action mesh: " + layer);
        }
        for (String group : List.of("handAttachments", "eyeAttachments")) {
            if (group.equals("eyeAttachments") && !settings.containsKey(group)) continue;
            JsonObject attachments = object(settings.get(group), group);
            keys(attachments, Set.of("l", "r"));
            for (var item : attachments.values()) {
                JsonObject attachment = object(item, "attachment");
                keys(attachment, Set.of("layer", "source"));
                if (!bindings.containsKey(string(attachment.get("layer"), "attachment layer")))
                    throw new IllegalArgumentException("Attachment layer is not an authored action mesh");
                JsonArray source = array(attachment.get("source"), "attachment source");
                if (source.size() != 2) throw new IllegalArgumentException("Attachment source must contain x/y");
                for (var coordinate : source) boundedNumber(coordinate, "attachment coordinate", 0, 10000);
            }
        }
        for (var role : roles.entrySet()) {
            Drawable mesh = meshes.get(bindings.get(role.getKey()));
            var grid = mesh.getGeometryGrid();
            if (!"DeformBodyZBreath".equals(mangledString(mesh, Drawable.class, "getParentDeformerId-")))
                throw new IllegalStateException("Action wing must share the body coordinate frame: " + role.getKey());
            if (grid == null) throw new IllegalStateException("Missing wing geometry keyforms: " + role.getKey());
            requireAxes(grid, Set.of("ParamWing" + role.getValue() + "Lift", "ParamWing" + role.getValue() + "Swing", "ParamWing" + role.getValue() + "Fold"));
            if (grid.getCells().size() != 45 || grid.getCells().stream().allMatch(cell -> allZero(cell.getForm().getPositionDeltas())))
                throw new IllegalStateException("Wing must retain its complete moving keyform grid: " + role.getKey());
            var rest = grid.getCells().stream().filter(cell -> neutral(grid, cell.getCoordinate())).findFirst().orElseThrow();
            if (!allZero(rest.getForm().getPositionDeltas())) throw new IllegalStateException("Wing neutral geometry changed: " + role.getKey());
        }
        if (settings.containsKey("beak")) {
            JsonObject beak = object(settings.get("beak"), "beak");
            for (String role : List.of("closed", "upper", "lower", "inner")) {
                Drawable mesh = meshes.get(bindings.get(string(beak.get(role), role)));
                var grid = mesh.getGeometryGrid();
                if (grid == null || grid.getCells().size() != 7 || !role.equals("closed") && grid.getCells().stream().allMatch(cell -> allZero(cell.getForm().getPositionDeltas())))
                    throw new IllegalStateException("Beak part lacks seven geometric keyforms: " + role);
                var rest = grid.getCells().stream().filter(cell -> neutral(grid, cell.getCoordinate())).findFirst().orElseThrow();
                if (!allZero(rest.getForm().getPositionDeltas())) throw new IllegalStateException("Beak neutral geometry changed: " + role);
                var opacity = mesh.getChannelGrids().get(FormChannel.OPACITY);
                for (var candidate : List.of(grid, opacity)) {
                    if (role.equals("closed")) {
                        for (KeyformAxis axis : candidate.getAxes())
                            if (!Set.of("ParamBeakOpen", "ParamMouthOpenY").contains(mangledString(axis, KeyformAxis.class, "getParameterId-")))
                                throw new IllegalStateException("Closed beak contains an unexpected parameter");
                    } else requireAxes(candidate, Set.of("ParamBeakOpen"));
                }
                var opacityRest = opacity.getCells().stream().filter(cell -> neutral(opacity, cell.getCoordinate())).findFirst().orElseThrow();
                if (!(opacityRest.getForm() instanceof ChannelValue.Scalar value) || value.getValue() != (role.equals("closed") ? 1f : 0f))
                    throw new IllegalStateException("Beak neutral visibility changed: " + role);
            }
        }
        if (settings.containsKey("busyEyes")) for (JsonElement item : array(settings.get("busyEyes"), "busyEyes")) {
            JsonObject eye = object(item, "busy eye");
            Drawable mesh = meshes.get(bindings.get(string(eye.get("layer"), "busy eye layer")));
            var grid = mesh.getGeometryGrid();
            if (grid == null || grid.getCells().size() != 3 || grid.getCells().stream().allMatch(cell -> allZero(cell.getForm().getPositionDeltas())))
                throw new IllegalStateException("Busy eye requires moving open/half/closed geometry");
            requireAxes(grid, Set.of(string(eye.get("eyeParameter"), "eyeParameter")));
            requireAxes(mesh.getChannelGrids().get(FormChannel.OPACITY), Set.of("ParamBusyEyes"));
            for (String role : List.of("layer", "normal")) {
                Drawable drawable = meshes.get(bindings.get(string(eye.get(role), role)));
                var opacity = drawable.getChannelGrids().get(FormChannel.OPACITY);
                requireAxes(opacity, Set.of("ParamBusyEyes"));
                for (var cell : opacity.getCells()) {
                    float key = opacity.getAxes().getFirst().getKeys()[cell.getCoordinate()[0]];
                    float expected = role.equals("normal") ? 1 - key : key;
                    if (!(cell.getForm() instanceof ChannelValue.Scalar value) || Math.abs(value.getValue() - expected) > 0.0001f)
                        throw new IllegalStateException("Normal and busy eye opacity must be complementary");
                }
            }
        }
        System.out.println("Full-body verification: four moving wing meshes, neutral geometry unchanged, real jaw geometry and prop attachments");
    }

    private static MeshSpace meshSpace(String name, BuiltRig rig, PipelineAnalysis analysis, Map<String, String> bindings) throws Exception {
        var sourceMatches = analysis.getSource().getLayers().stream().filter(layer -> layer.getName().equals(name)).toList();
        if (sourceMatches.size() != 1) throw new IllegalArgumentException("Full-body rig requires exactly one layer: " + name);
        String sourceId = mangledString(sourceMatches.getFirst(), SourceLayer.class, "getId-");
        Drawable found = null; String id = null;
        for (Drawable drawable : rig.getPuppet().getDrawables()) {
            String currentId = mangledString(drawable, Drawable.class, "getId-");
            if (!sourceId.equals(rig.getLayerIdByDrawableId().get(currentId))) continue;
            if (found != null) throw new IllegalStateException("Action layer must be one connected mesh: " + name);
            found = drawable; id = currentId;
        }
        if (found == null) throw new IllegalStateException("No drawable for action layer: " + name);
        float[] positions = found.getMesh().getPositions();
        float minX = Float.POSITIVE_INFINITY, minY = Float.POSITIVE_INFINITY, maxX = Float.NEGATIVE_INFINITY, maxY = Float.NEGATIVE_INFINITY;
        for (int i = 0; i < positions.length; i += 2) {
            minX = Math.min(minX, positions[i]); maxX = Math.max(maxX, positions[i]);
            minY = Math.min(minY, positions[i + 1]); maxY = Math.max(maxY, positions[i + 1]);
        }
        Bounds bounds = rig.getSourceBoundsByDrawableId().get(id);
        if (bounds == null || !(maxX > minX && maxY > minY)) throw new IllegalStateException("Action mesh lacks usable source coordinates: " + name);
        bindings.put(name, id);
        return new MeshSpace(found, id, positions, minX, minY, bounds.getWidth() / (maxX - minX), bounds.getHeight() / (maxY - minY), bounds);
    }

    private static float[] wingPoint(float x, float y, String side, float pivotX, float pivotY, float rootY,
            float lift, float swing, float fold, float elbowX, float elbowY, float foldDegrees) {
        float sign = side.equals("L") ? 1 : -1;
        float influence = smoothStep((y - rootY) / (pivotY - rootY));
        float band = Math.min(12f, (elbowY - pivotY) * 0.45f);
        float distal = smoothStep((y - elbowY + band) / (2 * band));
        double bend = Math.toRadians(sign * foldDegrees * fold * distal);
        float radial = 1 - 0.08f * fold * distal;
        float dx = (x - elbowX) * radial, dy = (y - elbowY) * radial;
        float bentX = elbowX + (float) (dx * Math.cos(bend) - dy * Math.sin(bend)) - pivotX;
        float bentY = elbowY + (float) (dx * Math.sin(bend) + dy * Math.cos(bend)) - pivotY;
        double angle = Math.toRadians(sign * (105f * lift + 25f * swing));
        float targetX = pivotX + (float) (bentX * Math.cos(angle) - bentY * Math.sin(angle));
        float targetY = pivotY + (float) (bentX * Math.sin(angle) + bentY * Math.cos(angle));
        return new float[] {x + (targetX - x) * influence, y + (targetY - y) * influence};
    }

    private static RigEditOverlay wingKeys(RigEditOverlay edits, MeshSpace mesh, String side, float pivotX, float pivotY,
            float rootY, float elbowX, float elbowY, float foldDegrees) {
        float maxMotion = 0;
        for (float lift : new float[] {0, 0.25f, 0.5f, 0.75f, 1})
            for (float swing : new float[] {-1, 0, 1}) for (float fold : new float[] {0, 0.5f, 1}) {
                var delta = new ArrayList<Float>(mesh.positions().length);
                for (int i = 0; i < mesh.positions().length; i += 2) {
                    float x = mesh.x(i), y = mesh.y(i);
                    float[] target = wingPoint(x, y, side, pivotX, pivotY, rootY, lift, swing, fold, elbowX, elbowY, foldDegrees);
                    boolean rest = lift == 0 && swing == 0 && fold == 0;
                    float dx = rest ? 0 : (target[0] - x) / mesh.scaleX();
                    float dy = rest ? 0 : (target[1] - y) / mesh.scaleY();
                    delta.add(dx); delta.add(dy);
                    maxMotion = Math.max(maxMotion, (float) Math.hypot(target[0] - x, target[1] - y));
                }
                var pose = new LinkedHashMap<String, Float>();
                pose.put("ParamWing" + side + "Lift", lift);
                pose.put("ParamWing" + side + "Swing", swing);
                pose.put("ParamWing" + side + "Fold", fold);
                edits = edits.setKeyform(new RigKeyformSetEdit(new RigTargetRef(RigTargetKind.ART_MESH, mesh.id(), null), pose,
                    new RigKeyformGeometryEdit(null, null, null, null, null, delta), null));
            }
        if (maxMotion < 5) throw new IllegalStateException("Wing binding has no usable movement: " + mesh.id());
        System.out.printf(Locale.ROOT, "Authored wing mesh: %s side=%s keyforms=45 max-motion=%.2f px%n", mesh.id(), side, maxMotion);
        return edits;
    }

    private static RigEditOverlay opacity(RigEditOverlay edits, String id, String parameter, float key, float value) {
        return edits.setKeyform(new RigKeyformSetEdit(new RigTargetRef(RigTargetKind.ART_MESH, id, null), Map.of(parameter, key),
            null, new RigKeyformChannelsEdit(value, null, null, null, null, null, null)));
    }

    private static RigEditOverlay authorBeak(RigEditOverlay edits, BuiltRig rig, PipelineAnalysis analysis, JsonObject settings,
            Map<String, String> bindings) throws Exception {
        keys(settings, Set.of("closed", "upper", "lower", "inner", "legacyHidden", "hingeY", "gapPx", "upperFlatten", "lowerScale", "innerBounds"));
        float hingeY = boundedNumber(settings.get("hingeY"), "beak hingeY", 0, analysis.getSource().getHeightPx());
        float gap = boundedNumber(settings.get("gapPx"), "beak gapPx", 2, 30);
        float flatten = boundedNumber(settings.get("upperFlatten"), "upperFlatten", 0, 0.6f);
        float lowerScale = boundedNumber(settings.get("lowerScale"), "lowerScale", 0.3f, 1);
        JsonArray innerBounds = array(settings.get("innerBounds"), "innerBounds");
        if (innerBounds.size() != 4) throw new IllegalArgumentException("innerBounds must contain left/top/width/height");
        float innerLeft = boundedNumber(innerBounds.get(0), "inner left", 0, analysis.getSource().getWidthPx());
        float innerTop = boundedNumber(innerBounds.get(1), "inner top", 0, analysis.getSource().getHeightPx());
        float innerWidth = boundedNumber(innerBounds.get(2), "inner width", 1, analysis.getSource().getWidthPx() - innerLeft);
        float innerHeight = boundedNumber(innerBounds.get(3), "inner height", 1, analysis.getSource().getHeightPx() - innerTop);
        edits = parameter(edits, "ParamBeakOpen", 0, 1);
        for (String role : List.of("closed", "upper", "lower", "inner")) {
            MeshSpace mesh = meshSpace(string(settings.get(role), role), rig, analysis, bindings);
            for (float open : new float[] {0, 0.05f, 0.1f, 0.25f, 0.5f, 0.75f, 1}) {
                float reveal = Math.max(0, Math.min(1, (open - 0.05f) / 0.05f));
                float alpha = role.equals("closed") ? 1 - reveal : reveal;
                var delta = new ArrayList<Float>(mesh.positions().length);
                for (int i = 0; i < mesh.positions().length; i += 2) {
                    float x = mesh.x(i), y = mesh.y(i), targetY = open == 0 ? y : switch (role) {
                        case "upper" -> y - (gap * 0.22f + Math.max(0, y - hingeY) * flatten) * open;
                        case "lower" -> hingeY + (y - hingeY) * (1 - (1 - lowerScale) * open) + gap * 0.78f * open;
                        case "inner" -> innerTop + innerHeight -
                            (1 - (y - mesh.canvas().getTop()) / mesh.canvas().getHeight()) * innerHeight * open;
                        default -> y;
                    };
                    float cavityWidth = innerWidth * (0.30f + 0.70f * smoothStep(open));
                    float targetX = role.equals("inner") && open != 0 ? innerLeft + innerWidth / 2 +
                        ((x - mesh.canvas().getLeft()) / mesh.canvas().getWidth() - 0.5f) * cavityWidth : x;
                    delta.add((targetX - x) / mesh.scaleX()); delta.add((targetY - y) / mesh.scaleY());
                }
                edits = edits.setKeyform(new RigKeyformSetEdit(new RigTargetRef(RigTargetKind.ART_MESH, mesh.id(), null),
                    Map.of("ParamBeakOpen", open), new RigKeyformGeometryEdit(null, null, null, null, null, delta),
                    new RigKeyformChannelsEdit(alpha, null,
                        role.equals("inner") ? List.of(0.60f, 0.58f, 0.55f) : null, null, null, null, null)));
            }
        }
        for (JsonElement item : array(settings.get("legacyHidden"), "legacyHidden")) {
            MeshSpace mesh = meshSpace(string(item, "legacy mouth layer"), rig, analysis, bindings);
            for (float key : new float[] {0, 1}) edits = opacity(edits, mesh.id(), "ParamBeakOpen", key, 0);
        }
        System.out.println("Authored bird beak: separate upper/lower jaws and inner mouth, seven geometry keys, no human mouth stretch");
        return edits;
    }

    // The lower head blends into the chest instead of rotating as a detached plate around its beak
    private static RigEditOverlay authorHeadConnection(RigEditOverlay edits, List<Deformer> deformers,
            Bounds character, JsonObject settings, int canvasWidth, int canvasHeight) throws Exception {
        keys(settings, Set.of("pivotX", "pivotY", "softAnchorStartY", "softAnchorEndY", "maxTiltDegrees", "pinFaceFeathers"));
        float pivotX = boundedNumber(settings.get("pivotX"), "pivotX", 0, canvasWidth);
        float pivotY = boundedNumber(settings.get("pivotY"), "pivotY", 0, canvasHeight);
        float startY = boundedNumber(settings.get("softAnchorStartY"), "softAnchorStartY", 0, canvasHeight);
        float endY = boundedNumber(settings.get("softAnchorEndY"), "softAnchorEndY", 0, canvasHeight);
        float maxTilt = boundedNumber(settings.get("maxTiltDegrees"), "maxTiltDegrees", 1, 20);
        boolean pinFace = settings.containsKey("pinFaceFeathers") && bool(settings.get("pinFaceFeathers"), "pinFaceFeathers");
        if (endY <= startY || pivotY < startY || pivotY > endY)
            throw new IllegalArgumentException("Head pivot must lie within an increasing soft-anchor band");
        Deformer.Rotation rotation = null;
        Deformer.Warp container = null;
        for (Deformer deformer : deformers) {
            String id = mangledString(deformer, Deformer.class, "getId-");
            if (id.equals("DeformHeadRotation") && deformer instanceof Deformer.Rotation value) rotation = value;
            if (id.equals("DeformHeadContainer") && deformer instanceof Deformer.Warp value) container = value;
        }
        if (rotation == null || container == null || rotation.getGeometryGrid() == null || container.getGeometryGrid() == null)
            throw new IllegalStateException("Expected PSD2Live head rotation and container geometry");
        if (!"DeformHeadRotation".equals(mangledString(container, Deformer.class, "getParent-")))
            throw new IllegalStateException("Head container must be the rotation's pixel-space child");
        var rotationGrid = rotation.getGeometryGrid();
        var headGrid = container.getGeometryGrid();
        requireAxes(rotationGrid, Set.of("ParamAngleZ"));
        requireAxes(headGrid, Set.of("ParamAngleX", "ParamAngleY"));
        var restRotation = rotationGrid.getCells().stream().filter(cell -> neutral(rotationGrid, cell.getCoordinate())).findFirst().orElseThrow().getForm();
        var restHead = headGrid.getCells().stream().filter(cell -> neutral(headGrid, cell.getCoordinate())).findFirst().orElseThrow().getForm().getControlPoints();
        float oldX = character.getLeft() + restRotation.getOriginX() * character.getWidth();
        float oldY = character.getTop() + restRotation.getOriginY() * character.getHeight();
        double baseAngle = Math.toRadians(rotation.getBaseAngle());
        float cosine = (float) Math.cos(baseAngle), sine = (float) Math.sin(baseAngle);
        float offsetX = cosine * (oldX - pivotX) + sine * (oldY - pivotY);
        float offsetY = -sine * (oldX - pivotX) + cosine * (oldY - pivotY);
        float localPivotX = (pivotX - character.getLeft()) / character.getWidth();
        float localPivotY = (pivotY - character.getTop()) / character.getHeight();
        var rotationTarget = new RigTargetRef(RigTargetKind.ROTATION_DEFORMER, "DeformHeadRotation", null);
        var headTarget = new RigTargetRef(RigTargetKind.WARP_DEFORMER, "DeformHeadContainer", null);
        float neutralError = 0, bottomMotion = 0, topMotion = 0;
        int anchoredControls = 0;
        for (int i = 0; i < restHead.length; i += 2)
            if (oldY + sine * restHead[i] + cosine * restHead[i + 1] >= endY) anchoredControls++;
        if (anchoredControls == 0)
            throw new IllegalArgumentException("Soft-anchor band must include at least one fully anchored head control point");
        int forms = 0;
        for (float z : new float[] {-30f, -15f, 0f, 15f, 30f}) {
            float angle = z / 30f * maxTilt;
            edits = edits.setKeyform(new RigKeyformSetEdit(rotationTarget, Map.of("ParamAngleZ", z),
                new RigKeyformGeometryEdit(null, localPivotX, localPivotY, angle, 1f, null), null));
            for (var cell : headGrid.getCells()) {
                var pose = coordinate(headGrid, cell.getCoordinate());
                pose.put("ParamAngleZ", z);
                float[] generated = cell.getForm().getControlPoints();
                var points = new ArrayList<Float>(generated.length);
                for (int i = 0; i < generated.length; i += 2) {
                    float restX = restHead[i] + offsetX, restY = restHead[i + 1] + offsetY;
                    float canvasY = oldY + sine * restHead[i] + cosine * restHead[i + 1];
                    float weight = smoothStep((canvasY - startY) / (endY - startY));
                    float x = restX + (generated[i] - restHead[i]) * (1f - weight);
                    float y = restY + (generated[i + 1] - restHead[i + 1]) * (1f - weight);
                    double counterAngle = Math.toRadians(-angle * weight);
                    float authoredX = (float) (x * Math.cos(counterAngle) - y * Math.sin(counterAngle));
                    float authoredY = (float) (x * Math.sin(counterAngle) + y * Math.cos(counterAngle));
                    points.add(authoredX); points.add(authoredY);
                    double theta = Math.toRadians(angle);
                    float rotatedX = (float) (authoredX * Math.cos(theta) - authoredY * Math.sin(theta));
                    float rotatedY = (float) (authoredX * Math.sin(theta) + authoredY * Math.cos(theta));
                    float motion = (float) Math.hypot(rotatedX - restX, rotatedY - restY);
                    if (weight == 1f) bottomMotion = Math.max(bottomMotion, motion);
                    if (weight == 0f) topMotion = Math.max(topMotion, motion);
                    if (z == 0f && neutral(headGrid, cell.getCoordinate())) {
                        float reconstructedX = pivotX + cosine * authoredX - sine * authoredY;
                        float reconstructedY = pivotY + sine * authoredX + cosine * authoredY;
                        neutralError = Math.max(neutralError, (float) Math.hypot(
                            reconstructedX - (oldX + cosine * restHead[i] - sine * restHead[i + 1]),
                            reconstructedY - canvasY));
                    }
                }
                edits = edits.setKeyform(new RigKeyformSetEdit(headTarget, pose,
                    new RigKeyformGeometryEdit(points, null, null, null, null, null), null));
                forms++;
            }
        }
        if (neutralError > 0.001f || bottomMotion > 0.001f || topMotion < 1f)
            throw new IllegalStateException("Chest rig must preserve neutral, anchor lower edge, and retain visible head movement");
        System.out.printf(Locale.ROOT,
            "Authored chest head rig: pivot=(%.2f,%.2f), soft-band=%.2f..%.2f, tilt=+/-%.2f, keyforms=%d, anchored-controls=%d, neutral-error=%.6f px, pinned-control-motion=%.6f px, upper-motion=%.3f px%n",
            pivotX, pivotY, startY, endY, maxTilt, forms, anchoredControls, neutralError, bottomMotion, topMotion);
        if (pinFace) edits = anchorFeatherSurface(edits, deformers, restHead, oldY, cosine, sine, startY, endY);
        return edits;
    }

    // The facial surface is a child warp and must not slide its feather edge across the anchored chest
    private static RigEditOverlay anchorFeatherSurface(RigEditOverlay edits, List<Deformer> deformers,
            float[] headRest, float oldPivotY, float cosine, float sine, float startY, float endY) throws Exception {
        Deformer.Warp face = null;
        for (Deformer deformer : deformers)
            if (deformer instanceof Deformer.Warp warp && "DeformFaceNinePose".equals(mangledString(deformer, Deformer.class, "getId-"))) face = warp;
        if (face == null || face.getGeometryGrid() == null ||
            !"DeformHeadContainer".equals(mangledString(face, Deformer.class, "getParent-")))
            throw new IllegalStateException("Expected facial surface directly below the head container");
        var grid = face.getGeometryGrid();
        requireAxes(grid, Set.of("ParamAngleX", "ParamAngleY"));
        float[] rest = grid.getCells().stream().filter(cell -> neutral(grid, cell.getCoordinate())).findFirst().orElseThrow().getForm().getControlPoints();
        float minX = Float.POSITIVE_INFINITY, minY = Float.POSITIVE_INFINITY;
        float maxX = Float.NEGATIVE_INFINITY, maxY = Float.NEGATIVE_INFINITY;
        for (int i = 0; i < headRest.length; i += 2) {
            minX = Math.min(minX, headRest[i]); maxX = Math.max(maxX, headRest[i]);
            minY = Math.min(minY, headRest[i + 1]); maxY = Math.max(maxY, headRest[i + 1]);
        }
        int pinned = 0, preserved = 0;
        float[] weights = new float[rest.length / 2];
        for (int i = 0; i < rest.length; i += 2) {
            float canvasY = oldPivotY + sine * (minX + rest[i] * (maxX - minX)) +
                cosine * (minY + rest[i + 1] * (maxY - minY));
            weights[i / 2] = smoothStep((canvasY - startY) / (endY - startY));
            if (weights[i / 2] == 1f) pinned++;
            if (weights[i / 2] == 0f) preserved++;
        }
        if (pinned == 0 || preserved == 0) throw new IllegalArgumentException("Feather surface band must anchor lower rows and preserve upper rows");
        var target = new RigTargetRef(RigTargetKind.WARP_DEFORMER, "DeformFaceNinePose", null);
        for (var cell : grid.getCells()) {
            float[] generated = cell.getForm().getControlPoints();
            var points = new ArrayList<Float>(generated.length);
            for (int i = 0; i < generated.length; i++) {
                float weight = weights[i / 2];
                float value = weight == 0f ? generated[i] : weight == 1f ? rest[i] : rest[i] + (generated[i] - rest[i]) * (1f - weight);
                if (neutral(grid, cell.getCoordinate()) && value != rest[i]) throw new IllegalStateException("Feather anchor changed neutral geometry");
                points.add(value);
            }
            edits = edits.setKeyform(new RigKeyformSetEdit(target, coordinate(grid, cell.getCoordinate()),
                new RigKeyformGeometryEdit(points, null, null, null, null, null), null));
        }
        System.out.printf(Locale.ROOT, "Authored feather surface anchor: keyforms=%d, fixed-controls=%d, unchanged-upper-controls=%d, neutral preserved exactly%n",
            grid.getCells().size(), pinned, preserved);
        return edits;
    }

    private static void requireAxes(KeyformGrid<?> grid, Set<String> expected) throws Exception {
        var actual = new HashSet<String>();
        for (KeyformAxis axis : grid.getAxes()) actual.add(mangledString(axis, KeyformAxis.class, "getParameterId-"));
        if (!actual.equals(expected)) throw new IllegalStateException("Unexpected head geometry axes: " + actual);
    }
    private static boolean neutral(KeyformGrid<?> grid, int[] coordinate) {
        for (int i = 0; i < coordinate.length; i++) if (grid.getAxes().get(i).getKeys()[coordinate[i]] != 0f) return false;
        return true;
    }
    private static Map<String, Float> coordinate(KeyformGrid<?> grid, int[] coordinate) throws Exception {
        var result = new LinkedHashMap<String, Float>();
        for (int i = 0; i < coordinate.length; i++) {
            var axis = grid.getAxes().get(i);
            result.put(mangledString(axis, KeyformAxis.class, "getParameterId-"), axis.getKeys()[coordinate[i]]);
        }
        return result;
    }
    private static float smoothStep(float value) {
        float t = Math.max(0f, Math.min(1f, value));
        return t * t * (3f - 2f * t);
    }
    private static float boundedNumber(JsonElement value, String name, float min, float max) {
        if (!(value instanceof JsonPrimitive primitive) || primitive.isString()) throw new IllegalArgumentException(name + " must be a number");
        float result = Float.parseFloat(primitive.getContent());
        if (!Float.isFinite(result) || result < min || result > max) throw new IllegalArgumentException(name + " must be in " + min + ".." + max);
        return result;
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
