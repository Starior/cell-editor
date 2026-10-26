using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CellEditor.Cells;

/// <summary>
/// One line of categories.json. Every field that is set must match (AND); inside a field any value may match (OR).
/// </summary>
public sealed class CategoryRule
{
    public string Name { get; set; } = "";

    /// <summary>#RRGGBB for the row mark in the tree.</summary>
    public string? Color { get; set; }

    /// <summary>Base record signatures: STAT, FURN, DOOR, ...</summary>
    public string[]? Types { get; set; }

    /// <summary>Text inside the model path under meshes\.</summary>
    public string[]? Path { get; set; }

    /// <summary>Text inside the base record editor id.</summary>
    public string[]? EditorId { get; set; }

    /// <summary>Keyword editor ids on the base record.</summary>
    public string[]? Keywords { get; set; }

    /// <summary>Matches only markers (true) or only non-markers (false).</summary>
    public bool? Marker { get; set; }

    public bool Matches(PlacedMesh mesh)
    {
        if (Marker is bool marker && marker != mesh.IsMarker)
        {
            return false;
        }

        if (Types is { Length: > 0 } && !Types.Any(type => type.Equals(mesh.BaseType, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (Path is { Length: > 0 })
        {
            var model = mesh.ModelPath.Replace('/', '\\');
            if (!Path.Any(part => model.Contains(part.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (EditorId is { Length: > 0 } && !EditorId.Any(part => mesh.BaseEditorId.Contains(part, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (Keywords is { Length: > 0 }
            && !Keywords.Any(keyword => mesh.BaseKeywords.Contains(keyword, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        return Marker != null || Types != null || Path != null || EditorId != null || Keywords != null;
    }
}

public sealed class CategoryFile
{
    public List<CategoryRule> Categories { get; set; } = new();

    /// <summary>Reference form key ("06C901:Skyrim.esm") to category name. Wins over the rules.</summary>
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class Categories
{
    public const string Other = "Other";

    static readonly string[] Palette =
    [
        "#C8A060", "#6FA8DC", "#93C47D", "#E06666", "#B4A7D6", "#F6B26B", "#76A5AF", "#D5A6BD", "#FFD966", "#A2C4C9",
    ];

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    Categories(string path, CategoryFile file)
    {
        Path = path;
        File = file;
    }

    public static Categories Empty => new(Locate(), new CategoryFile());

    public string Path { get; }

    public CategoryFile File { get; }

    /// <summary>Category names in file order, then Other.</summary>
    public IReadOnlyList<string> Names => File.Categories.Select(rule => rule.Name)
        .Concat(File.Overrides.Values)
        .Append(Other)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static Categories Load(string? path = null)
    {
        path ??= Locate();
        var file = System.IO.File.Exists(path)
            ? JsonSerializer.Deserialize<CategoryFile>(System.IO.File.ReadAllText(path), Options) ?? new CategoryFile()
            : new CategoryFile();
        file.Overrides = new Dictionary<string, string>(file.Overrides, StringComparer.OrdinalIgnoreCase);
        file.Categories.RemoveAll(rule => string.IsNullOrWhiteSpace(rule.Name));
        return new Categories(path, file);
    }

    public string Of(PlacedMesh mesh)
    {
        if (!mesh.Key.IsNull && File.Overrides.TryGetValue(mesh.Key.ToString(), out var picked))
        {
            return picked;
        }

        return File.Categories.FirstOrDefault(rule => rule.Matches(mesh))?.Name ?? Other;
    }

    public string ColorOf(string name)
    {
        var rule = File.Categories.FirstOrDefault(rule => rule.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && rule.Color != null);
        if (rule != null)
        {
            return rule.Color!;
        }

        if (name.Equals(Other, StringComparison.OrdinalIgnoreCase))
        {
            return "#6A6A6A";
        }

        var index = Names.ToList().FindIndex(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
        return Palette[Math.Max(index, 0) % Palette.Length];
    }

    /// <summary>The repo copy next to CellEditor.csproj, so edits land in git; the build folder otherwise.</summary>
    static string Locate()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CellEditor.csproj")))
            {
                return System.IO.Path.Combine(dir.FullName, "categories.json");
            }
        }

        return System.IO.Path.Combine(AppContext.BaseDirectory, "categories.json");
    }
}
