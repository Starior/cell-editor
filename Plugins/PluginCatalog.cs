using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace CellEditor.Plugins;

public sealed class PluginRow : INotifyPropertyChanged
{
    public required string FileName { get; init; }

    public required string FullPath { get; init; }

    public required string[] Masters { get; init; }

    public required bool EnabledInMo2 { get; init; }

    public bool Missing { get; init; }

    public bool CanCheck => !Missing;

    public string Label => Missing ? FileName + "  (file not found)" : FileName;

    public string MastersText => Masters.Length == 0 ? "No masters" : string.Join(", ", Masters);

    bool isChecked;

    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (isChecked == value)
            {
                return;
            }

            isChecked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public static class PluginCatalog
{
    public static IReadOnlyList<PluginRow> Load()
    {
        var layout = ReadLayout();
        var profileDir = Path.Combine(layout.Instance, "profiles", layout.Profile);
        var modsDir = Path.Combine(layout.Instance, "mods");
        var dataDir = Path.Combine(layout.Game, "Data");

        var order = ReadNames(Path.Combine(profileDir, "loadorder.txt"));
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ReadNames(Path.Combine(profileDir, "plugins.txt")))
        {
            var on = line.StartsWith('*');
            var name = on ? line[1..] : line;
            listed.Add(name);
            if (on)
            {
                enabled.Add(name);
            }
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(modsDir))
        {
            foreach (var line in File.ReadAllLines(Path.Combine(profileDir, "modlist.txt")))
            {
                // A disabled mod is not in the game's data folder, so its plugin copy must not win.
                if (line.Length < 2 || line[0] != '+')
                {
                    continue;
                }

                var dir = Path.Combine(modsDir, line[1..]);
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(file);
                    if (!ext.Equals(".esp", StringComparison.OrdinalIgnoreCase)
                        && !ext.Equals(".esm", StringComparison.OrdinalIgnoreCase)
                        && !ext.Equals(".esl", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(file);
                    if (!files.ContainsKey(fileName))
                    {
                        files[fileName] = file;
                    }
                }
            }
        }

        if (Directory.Exists(dataDir))
        {
            foreach (var file in Directory.EnumerateFiles(dataDir))
            {
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".esp", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".esm", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".esl", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = Path.GetFileName(file);
                if (!files.ContainsKey(name))
                {
                    files[name] = file;
                }
            }
        }

        var rows = new List<PluginRow>(order.Count);
        foreach (var name in order)
        {
            files.TryGetValue(name, out var path);
            var missing = path == null;
            string[] masters = [];
            if (!missing)
            {
                try
                {
                    masters = PluginHeader.ReadMasters(path!);
                }
                catch (Exception)
                {
                    masters = [];
                }
            }

            var on = !listed.Contains(name) || enabled.Contains(name);
            rows.Add(new PluginRow
            {
                FileName = name,
                FullPath = path ?? "",
                Masters = masters,
                EnabledInMo2 = on && !missing,
                Missing = missing,
                IsChecked = on && !missing,
            });
        }

        return rows;
    }

    public static IReadOnlyList<string> MeshRoots()
    {
        var layout = ReadLayout();
        var roots = new List<string>();
        var modlist = Path.Combine(layout.Instance, "profiles", layout.Profile, "modlist.txt");
        if (File.Exists(modlist))
        {
            foreach (var line in File.ReadAllLines(modlist))
            {
                if (line.Length < 2 || line[0] != '+')
                {
                    continue;
                }

                roots.Add(Path.Combine(layout.Instance, "mods", line[1..]));
            }
        }

        roots.Add(Path.Combine(layout.Game, "Data"));
        return roots;
    }

    public static string ModsDir => Path.Combine(CellEditor.Mo2Location.Require(), "mods");

    /// <summary>Game Data\meshes, when the Mod Organizer folder is already known.</summary>
    public static string? TryMeshesDirectory()
    {
        if (CellEditor.Mo2Location.Try() == null)
        {
            return null;
        }

        try
        {
            var meshes = Path.Combine(ReadLayout().Game, "Data", "meshes");
            return Directory.Exists(meshes) ? meshes : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    readonly record struct Layout(string Instance, string Profile, string Game);

    static Layout ReadLayout()
    {
        var instance = CellEditor.Mo2Location.Require();
        var ini = File.ReadAllLines(Path.Combine(instance, "ModOrganizer.ini"));
        var profile = ByteArray(ini, "selected_profile");
        if (string.IsNullOrWhiteSpace(profile))
        {
            var profilesDir = Path.Combine(instance, "profiles");
            var profiles = Directory.Exists(profilesDir) ? Directory.GetDirectories(profilesDir) : [];
            profile = profiles.Length == 1 ? Path.GetFileName(profiles[0]) : null;
        }

        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new InvalidOperationException("Mod Organizer has no selected profile.");
        }

        var game = ByteArray(ini, "gamePath");
        if (string.IsNullOrWhiteSpace(game))
        {
            throw new InvalidOperationException("Mod Organizer has no game path.");
        }

        return new Layout(instance, profile, game);
    }

    static string ProfileDir()
    {
        var layout = ReadLayout();
        return Path.Combine(layout.Instance, "profiles", layout.Profile);
    }

    /// <summary>
    /// Load rank of every archive the game opens: Skyrim.ini lists first, then each active plugin's
    /// "Name.bsa" / "Name - *.bsa" in load order. A higher rank is loaded later and wins.
    /// An archive missing from the map is not loaded by the game.
    /// </summary>
    public static IReadOnlyDictionary<string, int> ArchiveRanks()
    {
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rank = 0;
        var ini = Path.Combine(ProfileDir(), "Skyrim.ini");
        if (File.Exists(ini))
        {
            foreach (var key in new[] { "sResourceArchiveList=", "sResourceArchiveList2=" })
            {
                var line = File.ReadAllLines(ini).FirstOrDefault(text => text.TrimStart().StartsWith(key, StringComparison.OrdinalIgnoreCase));
                foreach (var name in line?.Split('=', 2)[1].Split(',') ?? [])
                {
                    ranks.TryAdd(name.Trim(), rank++);
                }
            }
        }

        foreach (var row in Load().Where(row => row.EnabledInMo2 && !row.Missing))
        {
            ranks.TryAdd(Path.GetFileNameWithoutExtension(row.FileName), 100_000 + rank++);
        }

        return ranks;
    }

    /// <summary>Plugins that live in "[MY] ..." mod folders, the ones authored for this build.</summary>
    public static IReadOnlyList<PluginRow> OwnPlugins()
    {
        return Load()
            .Where(row => !row.Missing && row.FullPath.Contains(@"\mods\[MY]", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Turns on a mod folder at the highest priority and its plugin at the end of the load order.
    /// MO2 shows it after a refresh (F5).
    /// </summary>
    public static void EnableNewMod(string modFolder, string pluginFile)
    {
        var profile = ProfileDir();
        var modlist = Path.Combine(profile, "modlist.txt");
        var mods = ReadLines(modlist);
        mods.RemoveAll(line => line.Length > 1 && line[1..].Equals(modFolder, StringComparison.OrdinalIgnoreCase));
        var top = mods.TakeWhile(line => line.StartsWith('#')).Count();
        mods.Insert(top, "+" + modFolder);
        WriteLines(modlist, mods);

        var plugins = Path.Combine(profile, "plugins.txt");
        var enabled = ReadLines(plugins);
        if (!enabled.Any(line => line.TrimStart('*').Equals(pluginFile, StringComparison.OrdinalIgnoreCase)))
        {
            enabled.Add("*" + pluginFile);
            WriteLines(plugins, enabled);
        }

        var loadorder = Path.Combine(profile, "loadorder.txt");
        var order = ReadLines(loadorder);
        if (!order.Any(line => line.Equals(pluginFile, StringComparison.OrdinalIgnoreCase)))
        {
            order.Add(pluginFile);
            WriteLines(loadorder, order);
        }
    }

    static List<string> ReadLines(string path)
    {
        return File.Exists(path) ? File.ReadAllLines(path).Where(line => line.Length > 0).ToList() : new List<string>();
    }

    // MO2 profile files are CRLF without a BOM.
    static void WriteLines(string path, IEnumerable<string> lines)
    {
        File.WriteAllText(path, string.Concat(lines.Select(line => line + "\r\n")), new System.Text.UTF8Encoding(false));
    }

    static List<string> ReadNames(string path)
    {
        var names = new List<string>();
        if (!File.Exists(path))
        {
            return names;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            names.Add(line);
        }

        return names;
    }

    static string? ByteArray(string[] lines, string key)
    {
        var prefix = key + "=@ByteArray(";
        foreach (var line in lines)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith(')'))
            {
                continue;
            }

            return line[prefix.Length..^1].Replace(@"\\", @"\");
        }

        return null;
    }
}
