using System.IO;

namespace CellEditor.Textures;

public sealed class TextureSource
{
    public required string ModName { get; init; }

    public required Func<byte[]?> Read { get; init; }
}

public static class AssetIndex
{
    static readonly object gate = new();
    static List<ModAssets>? mods;
    static string rootsKey = "";

    public static void Ensure(IReadOnlyList<string> roots)
    {
        var key = string.Join("|", roots);
        lock (gate)
        {
            if (mods != null && rootsKey == key)
            {
                return;
            }

            var ranks = CellEditor.Plugins.PluginCatalog.ArchiveRanks();
            mods = roots.Select(root => Build(root, ranks)).ToList();
            rootsKey = key;
        }
    }

    public static IReadOnlyList<TextureSource> Find(string relative)
    {
        var path = BsaArchive.Normalize(relative);
        List<ModAssets> snapshot;
        lock (gate)
        {
            snapshot = mods ?? [];
        }

        // The game's rule: every loose file (mod priority, highest first) beats every archive,
        // and among archives the one loaded later (plugin load order) wins.
        var hits = new List<TextureSource>();
        foreach (var mod in snapshot)
        {
            var loose = Path.Combine(mod.Root, path);
            if (File.Exists(loose))
            {
                var copy = loose;
                hits.Add(new TextureSource
                {
                    ModName = mod.Name,
                    Read = () => File.ReadAllBytes(copy),
                });
            }
        }

        var packed = snapshot
            .SelectMany(mod => mod.Archives.Select(archive => (mod, archive)))
            .Where(pair => pair.archive.Archive.Contains(path))
            .OrderByDescending(pair => pair.archive.Rank);
        foreach (var (mod, archive) in packed)
        {
            var found = archive.Archive;
            hits.Add(new TextureSource
            {
                ModName = mod.Name,
                Read = () => found.Read(path),
            });
        }

        return hits;
    }

    public static bool TryGet(string relative, out string modName, out string? loosePath, out Func<byte[]?>? read)
    {
        var hits = Find(relative);
        if (hits.Count == 0)
        {
            modName = "";
            loosePath = null;
            read = null;
            return false;
        }

        var winner = hits[0];
        modName = winner.ModName;
        var path = BsaArchive.Normalize(relative);
        loosePath = null;
        List<ModAssets> snapshot;
        lock (gate)
        {
            snapshot = mods ?? [];
        }

        foreach (var mod in snapshot)
        {
            if (!mod.Name.Equals(winner.ModName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var loose = Path.Combine(mod.Root, path);
            if (File.Exists(loose))
            {
                loosePath = loose;
            }

            break;
        }

        read = winner.Read;
        return true;
    }

    public static IReadOnlyList<string> ModNames(IReadOnlyList<string> roots)
    {
        Ensure(roots);
        lock (gate)
        {
            return (mods ?? []).Select(mod => mod.Name).ToList();
        }
    }

    static ModAssets Build(string root, IReadOnlyDictionary<string, int> ranks)
    {
        var name = DisplayName(root);

        var archives = new List<RankedArchive>();
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.bsa"))
            {
                if (RankOf(Path.GetFileName(file), ranks) is not int rank)
                {
                    continue;
                }

                var archive = BsaArchive.TryOpen(file);
                if (archive != null)
                {
                    archives.Add(new RankedArchive(archive, rank));
                }
            }
        }

        return new ModAssets(name, root, archives);
    }

    /// <summary>Rank of "Plugin.bsa" or "Plugin - Textures.bsa"; null when no ini entry or active plugin loads it.</summary>
    static int? RankOf(string fileName, IReadOnlyDictionary<string, int> ranks)
    {
        if (ranks.TryGetValue(fileName, out var listed))
        {
            return listed;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (ranks.TryGetValue(stem, out var plugin))
        {
            return plugin;
        }

        for (var cut = stem.LastIndexOf(" - ", StringComparison.Ordinal); cut > 0; cut = stem.LastIndexOf(" - ", cut - 1, StringComparison.Ordinal))
        {
            if (ranks.TryGetValue(stem[..cut], out var owner))
            {
                return owner;
            }
        }

        return null;
    }

    sealed record RankedArchive(BsaArchive Archive, int Rank);

    static string DisplayName(string root)
    {
        var name = Path.GetFileName(root.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name))
        {
            name = root;
        }

        // Last MeshRoots entry is the game install Data folder, not an MO2 mod under .../mods/.
        if (name.Equals("Data", StringComparison.OrdinalIgnoreCase)
            && !root.Replace('/', '\\').Contains(@"\mods\", StringComparison.OrdinalIgnoreCase))
        {
            return "Skyrim Data";
        }

        return name;
    }

    sealed class ModAssets(string name, string root, List<RankedArchive> archives)
    {
        public string Name { get; } = name;

        public string Root { get; } = root;

        public List<RankedArchive> Archives { get; } = archives;
    }
}
