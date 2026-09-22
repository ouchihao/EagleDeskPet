using System.Text.Json;
using DuckDeskPet.Core.CharacterPacks;

if (args.Length != 4 || args[0] != "validate" || args[2] != "--mode" || args[3] is not ("author-template" or "runtime"))
{
    Console.Error.WriteLine("Usage: CharacterPackTool validate <character-package.json> --mode author-template|runtime");
    return 64;
}
try
{
    string manifestPath = Path.GetFullPath(args[1]);
    var resources = new DirectoryCharacterPackResources(Path.GetDirectoryName(manifestPath)!);
    using var stream = resources.Open(Path.GetFileName(manifestPath)) ?? throw new FileNotFoundException("Missing manifest.", manifestPath);
    if (stream.Length > CharacterPackValidator.MaximumManifestBytes) throw new InvalidDataException("Manifest exceeds 1 MiB.");
    using var reader = new StreamReader(stream);
    var mode = args[3] == "runtime" ? CharacterValidationMode.Runtime : CharacterValidationMode.AuthorTemplate;
    var result = CharacterPackValidator.ValidateJson(reader.ReadToEnd(), mode, resources);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        manifestPath, mode = args[3], result.ContractValid, result.FilesVerified, result.RuntimeReady, result.ReadyForCompletePet,
        boundary = "Offline authoring check only. No SDK is executed; no model is installed or made available to the shop.",
        diagnostics = result.Diagnostics.Select(x => new { severity = x.Severity.ToString(), x.Code, x.Location, x.Message })
    }, new JsonSerializerOptions { WriteIndented = true }));
    return result.HasErrors ? 1 : mode == CharacterValidationMode.Runtime && !result.RuntimeReady ? 2 : 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
{
    Console.Error.WriteLine("PACKAGE_IO: " + ex.Message);
    return 1;
}
