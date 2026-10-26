using System.IO;

namespace CellEditor;

/// <summary>
/// The Mod Organizer instance folder (the one that contains ModOrganizer.ini).
/// A saved path lives in mo2.txt next to the program. Otherwise a parent of the
/// program folder is used when the program itself was placed inside the instance.
/// </summary>
public static class Mo2Location
{
    public static string? Try()
    {
        var saved = ReadSaved();
        if (saved != null)
        {
            return saved;
        }

        for (var dir = new DirectoryInfo(AppHome.Directory); dir != null; dir = dir.Parent)
        {
            if (IsInstance(dir.FullName))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    public static string Require()
    {
        return Try() ?? throw new InvalidOperationException(
            "Mod Organizer folder is not set. Choose the folder that contains ModOrganizer.ini.");
    }

    public static void Save(string directory)
    {
        if (!IsInstance(directory))
        {
            throw new InvalidOperationException("That folder has no ModOrganizer.ini.");
        }

        File.WriteAllText(AppHome.Mo2File, Path.GetFullPath(directory) + Environment.NewLine);
    }

    public static bool IsInstance(string directory)
    {
        return Directory.Exists(directory) && File.Exists(Path.Combine(directory, "ModOrganizer.ini"));
    }

    static string? ReadSaved()
    {
        if (!File.Exists(AppHome.Mo2File))
        {
            return null;
        }

        var line = File.ReadAllText(AppHome.Mo2File).Trim();
        return IsInstance(line) ? Path.GetFullPath(line) : null;
    }
}
