using System.IO;

namespace CellEditor.Textures;

public static class MeshCache
{
    public static readonly string Root = CellEditor.AppHome.MeshCache;

    public static string? Locate(IReadOnlyList<string> roots, string relativeUnderMeshes)
    {
        AssetIndex.Ensure(roots);
        var relative = "meshes\\" + relativeUnderMeshes.TrimStart('\\');
        if (!AssetIndex.TryGet(relative, out var modName, out var loosePath, out var read))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(loosePath))
        {
            return loosePath;
        }

        var bytes = read?.Invoke();
        if (bytes == null || bytes.Length == 0)
        {
            return null;
        }

        var dest = Path.Combine(Root, Safe(modName), relativeUnderMeshes);
        var directory = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(Long(directory));
        }

        var stored = Long(dest);
        if (!File.Exists(stored))
        {
            File.WriteAllBytes(stored, bytes);
        }

        return stored;
    }

    public static int DeleteUnused(IReadOnlyList<string> roots)
    {
        if (!Directory.Exists(Root))
        {
            return 0;
        }

        var keep = new HashSet<string>(AssetIndex.ModNames(roots).Select(Safe), StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(dir);
            if (keep.Contains(name))
            {
                continue;
            }

            Directory.Delete(dir, recursive: true);
            removed++;
        }

        return removed;
    }

    static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => Array.IndexOf(invalid, ch) >= 0 ? '_' : ch).ToArray();
        var safe = new string(chars).Trim();
        return safe.Length == 0 ? "mod" : safe;
    }

    static string Long(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full : @"\\?\" + full;
    }
}
