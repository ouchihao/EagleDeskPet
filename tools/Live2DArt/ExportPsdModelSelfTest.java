// SPDX-License-Identifier: GPL-3.0-or-later
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.util.Arrays;

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

        reject(input, evidence, "source-count", replace(validProfile, "\"expectedSourceLayers\": 15", "\"expectedSourceLayers\": 16"), "Source layer count differs");
        reject(input, evidence, "unknown-setting", replace(validProfile, "\"meshSpacing\": 32", "\"notASetting\": 32"), "Unknown configuration key");
        reject(input, evidence, "missing-layer", replace(validProfile, "\"irides-l\"", "\"absent-layer\""), "Expected exactly one source layer");
        reject(input, evidence, "invalid-atlas", replace(validProfile, "\"atlasSize\": 2048", "\"atlasSize\": 777"), "atlasSize must be a power of two");
        reject(input, evidence, "wrong-eye-parameter", replace(validProfile, "\"requiredGeometryParameter\": \"ParamEyeLOpen\"", "\"requiredGeometryParameter\": \"ParamBreath\""), "Eye mesh is not bound");
        reject(input, evidence, "unsupported-parent", replace(validProfile, "\"name\": \"footwear-l\", \"parent\": null", "\"name\": \"footwear-l\", \"parent\": \"../../escape\""), "Only explicit null/root parent");
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
    private static byte[] digest(Path path) throws Exception {
        return MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(path));
    }
    private static void mark(String description) {
        passed++;
        System.out.println("PASS " + description);
    }
}
