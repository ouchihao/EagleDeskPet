using System.IO;
using System.Text.Json;

namespace DuckDeskPet;

internal static class HybridActionManifest
{
    internal sealed record Route(string Backend, string? Directory);
    internal static Route Resolve(JsonElement manifest, string outfit, IEnumerable<string> expectedOutfits)
    {
        if (manifest.GetProperty("version").GetInt32() != 1 || manifest.GetProperty("width").GetInt32() != 384 ||
            manifest.GetProperty("height").GetInt32() != 346 || manifest.GetProperty("frameCount").GetInt32() != 121 ||
            manifest.GetProperty("durationSeconds").GetDouble() != 2)
            throw new InvalidDataException("Hybrid action pack must declare 384x346 / 121 frames / 2 seconds / version 1");
        var properties = manifest.EnumerateObject().Select(item => item.Name).ToArray();
        if (properties.Distinct(StringComparer.Ordinal).Count() != properties.Length)
            throw new InvalidDataException("Hybrid manifest has duplicate properties");
        var outfits = manifest.GetProperty("outfits").EnumerateObject().ToArray();
        var names = outfits.Select(item => item.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            !expectedOutfits.ToHashSet(StringComparer.Ordinal).SetEquals(names))
            throw new InvalidDataException("Hybrid action pack must declare each installed outfit exactly once");
        var routes = new Dictionary<string, Route>(StringComparer.Ordinal);
        foreach (var entry in outfits)
        {
            string? backend = entry.Value.GetProperty("route").GetString();
            if (backend == "live2d") { routes.Add(entry.Name, new(backend, null)); continue; }
            if (backend != "native-raster") throw new InvalidDataException("Unknown hybrid action route: " + backend);
            string? directory = entry.Value.GetProperty("directory").GetString();
            if (string.IsNullOrEmpty(directory) || directory is "." or ".." ||
                directory.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')))
                throw new InvalidDataException("Hybrid outfit directory must be one local path component");
            routes.Add(entry.Name, new(backend, directory));
        }
        var directories = routes.Values.Where(route => route.Directory is not null).Select(route => route.Directory).ToArray();
        if (directories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != directories.Length)
            throw new InvalidDataException("Hybrid outfits must use their own matching frame suites");
        return routes[outfit];
    }
}
