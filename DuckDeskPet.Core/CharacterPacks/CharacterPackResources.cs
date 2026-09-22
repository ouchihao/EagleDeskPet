using System.Text.RegularExpressions;

namespace DuckDeskPet.Core.CharacterPacks;

public static partial class CharacterPackPath
{
    public static bool IsSafe(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || !SafeCharacters().IsMatch(path)) return false;
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.')) return false;
            string basename = segment.Split('.')[0];
            if (ReservedDevice().IsMatch(basename)) return false;
        }
        return true;
    }

    public static string ResolveReference(string documentPath, string reference)
    {
        if (!IsSafe(documentPath) || !IsSafe(reference)) throw new InvalidDataException("Package paths must be safe relative forward-slash paths.");
        int separator = documentPath.LastIndexOf('/');
        string path = separator < 0 ? reference : documentPath[..(separator + 1)] + reference;
        if (!IsSafe(path)) throw new InvalidDataException("The resolved package reference is invalid.");
        return path;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCharacters();
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDevice();
}

/// <summary>Read-only local authoring directory; reparse points/symlinks are rejected, including the root</summary>
public sealed class DirectoryCharacterPackResources : ICharacterPackResources
{
    private readonly string _root;
    public DirectoryCharacterPackResources(string directory)
    {
        _root = Path.GetFullPath(directory);
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException(_root);
        // Also reject symlinked ancestors, not only the final package directory
        for (var cursor = new DirectoryInfo(_root); cursor is not null; cursor = cursor.Parent)
            RejectLink(cursor.FullName);
    }

    public Stream? Open(string safeRelativePath)
    {
        if (!CharacterPackPath.IsSafe(safeRelativePath)) throw new InvalidDataException("Unsafe package path: " + safeRelativePath);
        string current = _root;
        foreach (string segment in safeRelativePath.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) return null;
            RejectLink(current);
        }
        return File.Exists(current) ? new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Symlinks/reparse points are not package resources: " + path);
    }
}
