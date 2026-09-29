// SPDX-License-Identifier: GPL-3.0-or-later
import io.github.psd2live.core.*;
import kotlinx.serialization.json.*;
import org.umamo.format.art.SourceLayer;
import org.umamo.runtime.model.Drawable;
import org.umamo.runtime.model.Deformer;
import org.umamo.runtime.model.KeyformAxis;
import org.umamo.runtime.model.KeyformGrid;
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
        keys(profile, Set.of("schemaVersion", "psd2liveVersion", "expectedSourceLayers", "config", "layers", "requiredParameters", "headConnection"));
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
        RigEditOverlay edits = new RigEditOverlay();
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
        }
        if (profile.containsKey("headConnection"))
            edits = authorHeadConnection(edits, preview.getRig().getPuppet().getDeformers(),
                preview.getAnalysis().getAnchors().getCharacter(),
                object(profile.get("headConnection"), "headConnection"),
                analysis.getSource().getWidthPx(), analysis.getSource().getHeightPx());
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
            .append("\n  \"chestAnchoredHead\": ").append(profile.containsKey("headConnection")).append(',')
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

    private static PipelineConfig configure(PipelineConfig base, JsonObject settings,
            Map<String, LayerClassificationOverride> layers, Map<String, String> parents) throws Exception {
        Set<String> supported = Set.of("atlasSize", "meshSpacing", "headTurnStrength", "bodyStrength", "featureDisplacementEnabled", "mouthOutlineEnabled", "generatePhysics");
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
        if (settings.containsKey("featureDisplacementEnabled")) values[14] = bool(settings.get("featureDisplacementEnabled"), "featureDisplacementEnabled");
        if (settings.containsKey("mouthOutlineEnabled")) values[15] = bool(settings.get("mouthOutlineEnabled"), "mouthOutlineEnabled");
        if (settings.containsKey("generatePhysics")) values[25] = bool(settings.get("generatePhysics"), "generatePhysics");
        values[40] = layers;
        values[43] = parents;
        return (PipelineConfig) copy.invoke(base, values);
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
