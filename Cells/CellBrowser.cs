using System.IO;
using System.Numerics;
using CellEditor.Nif;
using CellEditor.Textures;
using CellEditor.Plugins;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace CellEditor.Cells;

public sealed class CellEntry
{
    public required FormKey Key { get; init; }

    public required string EditorId { get; init; }

    public string? Name { get; init; }

    public required string SourcePlugin { get; init; }

    public required bool Interior { get; init; }

    public string Label
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(Name) ? EditorId : EditorId + "   " + Name;
            return title + "   " + SourcePlugin;
        }
    }
}

public sealed class PlacedPart
{
    public required IReadOnlyList<Vector3> Positions { get; init; }

    public required IReadOnlyList<int> TriangleIndices { get; init; }

    public required IReadOnlyList<Vector2> Uvs { get; init; }

    public IReadOnlyList<Vector3> Normals { get; init; } = [];

    public bool DoubleSided { get; init; }

    public string? Diffuse { get; init; }

    public string? NormalMap { get; init; }
}

/// <summary>A base record with a model, offered when swapping the model of a placed object.</summary>
public sealed class ModelEntry
{
    public required FormKey Key { get; init; }

    public required string FormId { get; init; }

    public required string EditorId { get; init; }

    public string Name { get; init; } = "";

    /// <summary>Path under meshes\, as the base record stores it.</summary>
    public required string ModelPath { get; init; }

    public required string Plugin { get; init; }

    public string Label => (Name.Length == 0 ? EditorId : EditorId + "  " + Name) + "   " + ModelPath;
}

public sealed class PlacedMesh
{
    public FormKey Key { get; init; }

    public required string FormId { get; init; }

    public required string Plugin { get; init; }

    public required string ModelName { get; init; }

    /// <summary>Path under meshes\ of the drawn model.</summary>
    public string ModelPath { get; init; } = "";

    /// <summary>Base record editor id, plus the in-game name when the base has one.</summary>
    public string BaseName { get; init; } = "";

    public FormKey BaseKey { get; init; }

    /// <summary>"Enable parent ... (opposite)" from the winning record, empty without one.</summary>
    public string EnableParent { get; init; } = "";

    /// <summary>Base record signature: STAT, FURN, DOOR, ...</summary>
    public string BaseType { get; init; } = "";

    public string BaseEditorId { get; init; } = "";

    /// <summary>Editor ids of the base record's keywords.</summary>
    public IReadOnlyList<string> BaseKeywords { get; init; } = [];

    /// <summary>Reference point used to draw the mesh, in Skyrim units and radians.</summary>
    public PlacementView? Placement { get; init; }

    public int Number { get; set; }

    public bool Disabled { get; init; }

    public bool IsMarker { get; init; }

    public required IReadOnlyList<PlacedPart> Parts { get; init; }

    /// <summary>Newest plugin first. The first entry is the one the load order uses.</summary>
    public required IReadOnlyList<PlacedVersion> Versions { get; init; }

    public string Label => FormId + "   " + Number;

    public IEnumerable<Vector3> Positions => Parts.SelectMany(part => part.Positions);
}

public sealed class PlacedVersion
{
    public required string Plugin { get; init; }

    public required bool Disabled { get; init; }

    public string ModelName { get; init; } = "";

    public string ModelPath { get; init; } = "";

    /// <summary>This plugin's own reference point, in Skyrim units and radians.</summary>
    public PlacementView? Placement { get; init; }

    public required IReadOnlyList<PlacedPart> Parts { get; init; }

    public string Label => Disabled ? Plugin + "   disabled" : Plugin;
}

public sealed record DroppedRef(FormKey Key, string Plugin, string Reason);

public sealed class CellLoad
{
    public required string Title { get; init; }

    public required IReadOnlyList<PlacedMesh> Meshes { get; init; }

    public int Drawn { get; init; }

    public int MissingMeshes { get; init; }

    public IReadOnlyList<string> Missing { get; init; } = [];

    public int Skipped { get; init; }

    /// <summary>References that are not drawn, with the reason.</summary>
    public IReadOnlyList<DroppedRef> Dropped { get; init; } = [];

    /// <summary>Plugin that defines the cell record.</summary>
    public string CellPlugin { get; init; } = "";

    /// <summary>Every open plugin, load order.</summary>
    public IReadOnlyList<string> LoadOrder { get; init; } = [];

    public IReadOnlyDictionary<string, string[]> Masters { get; init; } = new Dictionary<string, string[]>();
}

public sealed partial class CellBrowser : IDisposable
{
    readonly List<ISkyrimModGetter> mods = new();
    readonly Dictionary<ModKey, Dictionary<FormKey, RawPlacement>> fallbacks = new();
    readonly List<CellEntry> cells = new();
    readonly List<ModelEntry> models = new();
    readonly object gate = new();
    ILinkCache? linkCache;
    bool disposed;

    public int ModelCount
    {
        get
        {
            lock (gate)
            {
                return models.Count;
            }
        }
    }

    /// <summary>Base records whose editor id, name, form id, or model path contains every word of the text.</summary>
    public IReadOnlyList<ModelEntry> SearchModels(string text, int limit)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return [];
        }

        lock (gate)
        {
            return models
                .Where(model => words.All(word => Matches(model, word)))
                .OrderBy(model => model.EditorId.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(model => model.EditorId.Length)
                .Take(limit)
                .ToArray();
        }
    }

    static bool Matches(ModelEntry model, string word)
    {
        return model.EditorId.Contains(word, StringComparison.OrdinalIgnoreCase)
            || model.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
            || model.FormId.Contains(word, StringComparison.OrdinalIgnoreCase)
            || model.ModelPath.Contains(word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads a model and places it at a reference pose, the same way a cell reference is drawn.
    /// Returns null with a reason when the mesh is missing or unreadable.
    /// </summary>
    public static IReadOnlyList<PlacedPart>? PlaceModel(string modelPath, PlacementView pose, IReadOnlyList<string> meshRoots, out string error)
    {
        error = "";
        var file = ResolveMesh(meshRoots, modelPath);
        if (file == null)
        {
            error = "Mesh not found: " + modelPath;
            return null;
        }

        NifMesh nif;
        try
        {
            nif = NifReader.Read(file, toViewSpace: false);
        }
        catch (Exception ex)
        {
            error = modelPath + ": " + ex.Message;
            return null;
        }

        var placed = TransformPose(nif, "", "", Path.GetFileNameWithoutExtension(modelPath),
            new Pose(pose.X, pose.Y, pose.Z, pose.Rx, pose.Ry, pose.Rz, pose.Scale));
        return placed.Parts;
    }

    public IReadOnlyList<CellEntry> Search(string text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var needle = text.Trim();
        lock (gate)
        {
            return cells
                .Where(cell => cell.EditorId.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || (cell.Name?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(limit)
                .ToArray();
        }
    }

    public int CellCount
    {
        get
        {
            lock (gate)
            {
                return cells.Count;
            }
        }
    }

    public void Open(IReadOnlyList<PluginRow> plugins, Action<string> log, CancellationToken cancel)
    {
        DisposeMods();
        var opened = new List<ISkyrimModGetter>();
        var paths = new Dictionary<ModKey, string>();
        foreach (var plugin in plugins)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var mod = SkyrimMod.CreateFromBinaryOverlay(plugin.FullPath, SkyrimRelease.SkyrimSE);
                opened.Add(mod);
                paths[mod.ModKey] = plugin.FullPath;
            }
            catch (Exception ex)
            {
                log("Could not read " + plugin.FileName + ": " + ex.Message);
            }
        }

        lock (gate)
        {
            mods.AddRange(opened);
            linkCache = mods.ToLoadOrder().ToImmutableLinkCache();
        }

        var seen = new HashSet<FormKey>();
        for (var i = 0; i < opened.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var mod = opened[i];
            var added = 0;
            try
            {
                foreach (var block in mod.Cells)
                {
                    foreach (var sub in block.SubBlocks)
                    {
                        foreach (var cell in sub.Cells)
                        {
                            if (Add(cell, mod.ModKey.FileName.String, interior: true, seen))
                            {
                                added++;
                            }
                        }
                    }
                }

                foreach (var world in mod.Worldspaces)
                {
                    foreach (var block in world.SubCells)
                    {
                        foreach (var sub in block.Items)
                        {
                            foreach (var cell in sub.Items)
                            {
                                if (Add(cell, mod.ModKey.FileName.String, interior: false, seen))
                                {
                                    added++;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log(mod.ModKey.FileName + " cells skipped: " + ex.Message);
                if (paths.TryGetValue(mod.ModKey, out var path))
                {
                    try
                    {
                        var raw = RefrScan.Read(mod, path);
                        lock (gate)
                        {
                            fallbacks[mod.ModKey] = raw;
                        }

                        log(mod.ModKey.FileName + "  " + raw.Count + " placed overrides read directly.");
                    }
                    catch (Exception scanEx)
                    {
                        log(mod.ModKey.FileName + " placed overrides skipped: " + scanEx.Message);
                    }
                }
            }

            log(mod.ModKey.FileName + "  " + added + " new cells  (" + (i + 1) + "/" + opened.Count + ")");
        }

        IndexModels(opened, log, cancel);
    }

    void IndexModels(IReadOnlyList<ISkyrimModGetter> opened, Action<string> log, CancellationToken cancel)
    {
        var found = new List<ModelEntry>();
        var seen = new HashSet<FormKey>();
        for (var i = opened.Count - 1; i >= 0; i--)
        {
            cancel.ThrowIfCancellationRequested();
            var mod = opened[i];
            var plugin = mod.ModKey.FileName.String;
            try
            {
                foreach (var record in ModeledRecords(mod))
                {
                    if (!seen.Add(record.FormKey) || record is not IModeledGetter modeled)
                    {
                        continue;
                    }

                    var path = ModelPath(modeled);
                    if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var name = record is INamedGetter named && !string.IsNullOrWhiteSpace(named.Name) ? named.Name.Trim() : "";
                    found.Add(new ModelEntry
                    {
                        Key = record.FormKey,
                        FormId = record.FormKey.ID.ToString("X8"),
                        EditorId = record.EditorID ?? "",
                        Name = name,
                        ModelPath = path,
                        Plugin = plugin,
                    });
                }
            }
            catch (Exception ex)
            {
                log(plugin + " models skipped: " + ex.Message);
            }
        }

        lock (gate)
        {
            models.Clear();
            models.AddRange(found);
        }

        log("Model index ready. " + found.Count + " base objects with a mesh.");
    }

    static IEnumerable<IMajorRecordGetter> ModeledRecords(ISkyrimModGetter mod)
    {
        return mod.Statics.Cast<IMajorRecordGetter>()
            .Concat(mod.MoveableStatics)
            .Concat(mod.Furniture)
            .Concat(mod.Doors)
            .Concat(mod.Containers)
            .Concat(mod.Activators)
            .Concat(mod.TalkingActivators)
            .Concat(mod.Lights)
            .Concat(mod.MiscItems)
            .Concat(mod.Florae)
            .Concat(mod.Trees)
            .Concat(mod.Books)
            .Concat(mod.Ingestibles)
            .Concat(mod.Ingredients)
            .Concat(mod.Keys)
            .Concat(mod.Weapons)
            .Concat(mod.Ammunitions)
            .Concat(mod.SoulGems)
            .Concat(mod.AlchemicalApparatuses);
    }

    public CellLoad Load(CellEntry entry, IReadOnlyList<string> meshRoots, Action<string>? progress = null)
    {
        ILinkCache cache;
        List<ISkyrimModGetter> order;
        lock (gate)
        {
            cache = linkCache ?? throw new InvalidOperationException("No plugins are open.");
            order = mods.ToList();
        }

        var history = new Dictionary<FormKey, List<SeenOverride>>();
        foreach (var mod in order)
        {
            var plugin = mod.ModKey.FileName.String;
            try
            {
                if (TryFindCell(mod, entry.Key, entry.Interior, out var cell))
                {
                    TakePlaced(plugin, cell, history);
                }
            }
            catch (Exception)
            {
                NoteDisabled(mod.ModKey, history);
            }
        }

        var startStates = new Dictionary<FormKey, bool>();
        foreach (var versions in history.Values)
        {
            foreach (var version in versions)
            {
                if (version.Ref != null)
                {
                    version.Disabled = StartsDisabled(version.Ref, history, cache, startStates);
                }
            }
        }

        var geometry = new Dictionary<string, NifMesh>(StringComparer.OrdinalIgnoreCase);
        var meshes = new List<PlacedMesh>();
        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropped = new List<DroppedRef>();
        var seenRefs = 0;
        var refTotal = history.Count;
        foreach (var (key, overrides) in history)
        {
            seenRefs++;
            if (progress != null && (seenRefs == refTotal || seenRefs % 40 == 0))
            {
                progress("Reading references " + seenRefs + " / " + refTotal);
            }

            var versions = new List<PlacedVersion>();
            NifMesh? latestNif = null;
            string? modelName = null;
            var modelPath = "";
            var reason = "";
            foreach (var version in overrides)
            {
                if (version.Ref != null && !version.Disabled)
                {
                    if (!TryNif(version, cache, meshRoots, geometry, missing, out var nif, out var loadedName, out var loadedPath, out var failed))
                    {
                        reason = failed;
                        continue;
                    }

                    latestNif = nif;
                    modelName = loadedName;
                    modelPath = loadedPath;
                    versions.Add(VersionAt(version, nif, loadedName, loadedPath));
                    continue;
                }

                if (version.Disabled || version.Ref == null)
                {
                    versions.Add(new PlacedVersion
                    {
                        Plugin = version.Plugin,
                        Disabled = true,
                        Parts = [],
                        Placement = PlacementOf(version.Ref),
                    });
                }
            }

            var winner = overrides[^1];
            if (versions.Count == 0)
            {
                dropped.Add(new DroppedRef(key, winner.Plugin, reason));
                continue;
            }

            IReadOnlyList<PlacedPart> parts = [];
            if (!winner.Disabled && latestNif != null && modelName != null)
            {
                var pose = WinningPose(overrides);
                var placed = TransformPose(latestNif, key.ID.ToString("X8"), winner.Plugin, modelName, pose);
                parts = placed.Parts;
                if (versions[^1].Plugin == winner.Plugin)
                {
                    versions[^1] = new PlacedVersion
                    {
                        Plugin = winner.Plugin,
                        Disabled = false,
                        ModelName = modelName,
                        ModelPath = modelPath,
                        Parts = parts,
                        Placement = PlacementOf(pose),
                    };
                }
            }

            if (!winner.Disabled && parts.Count == 0)
            {
                dropped.Add(new DroppedRef(key, winner.Plugin, reason));
                continue;
            }

            if (winner.Disabled && modelName == null)
            {
                foreach (var version in overrides)
                {
                    if (version.Ref != null)
                    {
                        modelName = ModelNameOf(version.Ref, cache) ?? modelName;
                    }
                }
            }

            if (winner.Disabled && modelName == null && !versions.Any(version => version.Parts.Count > 0))
            {
                continue;
            }

            var listed = versions.AsEnumerable().Reverse().ToList();
            var drawnPose = WinningPose(overrides);
            var baseRecord = BaseRecordOf(overrides, cache);
            var mesh = new PlacedMesh
            {
                Key = key,
                FormId = key.ID.ToString("X8"),
                Plugin = winner.Plugin,
                ModelName = modelName ?? "",
                ModelPath = modelPath,
                BaseName = BaseNameOf(overrides, cache),
                BaseKey = baseRecord?.FormKey ?? FormKey.Null,
                EnableParent = EnableParentText(winner.Ref),
                BaseType = baseRecord == null ? "" : SignatureOf(baseRecord),
                BaseEditorId = baseRecord?.EditorID ?? "",
                BaseKeywords = baseRecord == null ? [] : KeywordsOf(baseRecord, cache),
                Placement = PlacementOf(winner.Ref) ?? PlacementOf(drawnPose),
                Disabled = winner.Disabled,
                IsMarker = IsMarkerPlacement(winner.Ref, cache),
                Parts = parts,
                Versions = listed,
            };
            numbers.TryGetValue(winner.Plugin, out var number);
            mesh.Number = number + 1;
            numbers[winner.Plugin] = mesh.Number;
            meshes.Add(mesh);
        }

        var name = string.IsNullOrWhiteSpace(entry.Name) ? entry.EditorId : entry.EditorId + " — " + entry.Name;
        return new CellLoad
        {
            Title = name,
            Meshes = meshes,
            Drawn = meshes.Count(mesh => !mesh.Disabled),
            MissingMeshes = missing.Count,
            Missing = missing.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
            Skipped = meshes.Count(mesh => mesh.Disabled),
            Dropped = dropped,
            CellPlugin = entry.Key.ModKey.FileName.String,
            LoadOrder = order.Select(mod => mod.ModKey.FileName.String).ToList(),
            Masters = order
                .GroupBy(mod => mod.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().ModHeader.MasterReferences.Select(master => master.Master.FileName.String).ToArray(),
                    StringComparer.OrdinalIgnoreCase),
        };
    }

    public void Dispose()
    {
        disposed = true;
        DisposeMods();
    }

    bool Add(ICellGetter cell, string plugin, bool interior, HashSet<FormKey> seen)
    {
        if (string.IsNullOrWhiteSpace(cell.EditorID) || !seen.Add(cell.FormKey))
        {
            return false;
        }

        var entry = new CellEntry
        {
            Key = cell.FormKey,
            EditorId = cell.EditorID,
            Name = cell.Name?.String,
            SourcePlugin = plugin,
            Interior = interior,
        };
        lock (gate)
        {
            if (disposed)
            {
                return false;
            }

            cells.Add(entry);
        }

        return true;
    }

    void NoteDisabled(ModKey mod, Dictionary<FormKey, List<SeenOverride>> history)
    {
        Dictionary<FormKey, RawPlacement>? raws;
        lock (gate)
        {
            fallbacks.TryGetValue(mod, out raws);
        }

        if (raws == null)
        {
            return;
        }

        var plugin = mod.FileName.String;
        foreach (var pair in raws)
        {
            if (!history.TryGetValue(pair.Key, out var versions))
            {
                continue;
            }

            var raw = pair.Value;
            if (raw.Disabled || (raw.HasPosition && raw.Z <= -29000))
            {
                versions.Add(new SeenOverride { Plugin = plugin, Disabled = true });
            }
        }
    }

    static void TakePlaced(string plugin, ICellGetter cell, Dictionary<FormKey, List<SeenOverride>> history)
    {
        Remember(plugin, cell.Temporary, history);
        Remember(plugin, cell.Persistent, history);
    }

    static void Remember(string plugin, IEnumerable<IPlacedGetter> items, Dictionary<FormKey, List<SeenOverride>> history)
    {
        foreach (var item in items)
        {
            if (item is not IPlacedObjectGetter refr)
            {
                continue;
            }

            if (!history.TryGetValue(refr.FormKey, out var versions))
            {
                versions = new List<SeenOverride>();
                history[refr.FormKey] = versions;
            }

            versions.Add(new SeenOverride
            {
                Plugin = plugin,
                Ref = refr,
                Disabled = IsSunk(refr),
            });
        }
    }

    static bool TryNif(
        SeenOverride version,
        ILinkCache cache,
        IReadOnlyList<string> meshRoots,
        Dictionary<string, NifMesh> geometry,
        HashSet<string> missing,
        out NifMesh nif,
        out string modelName,
        out string modelPath,
        out string reason)
    {
        nif = null!;
        modelName = "";
        modelPath = "";
        reason = "";
        var refr = version.Ref;
        if (refr == null)
        {
            reason = "no reference record";
            return false;
        }

        if (!refr.Base.TryResolve(cache, out var baseRecord))
        {
            reason = "base " + refr.Base.FormKey + " not found";
            return false;
        }

        if (baseRecord is not IModeledGetter modeled)
        {
            reason = SignatureOf(baseRecord) + " base has no model";
            return false;
        }

        var relative = ModelPath(modeled);
        if (relative == null)
        {
            reason = SignatureOf(baseRecord) + " base has no model path";
            return false;
        }

        modelName = Path.GetFileNameWithoutExtension(relative);
        modelPath = relative;
        var file = ResolveMesh(meshRoots, relative);
        if (file == null)
        {
            missing.Add(relative);
            reason = "mesh not found: " + relative;
            return false;
        }

        if (!geometry.TryGetValue(file, out nif!))
        {
            try
            {
                nif = NifReader.Read(file, toViewSpace: false);
                geometry[file] = nif;
            }
            catch (Exception ex)
            {
                missing.Add(relative);
                reason = "mesh not readable: " + relative + " (" + ex.Message + ")";
                return false;
            }
        }

        if (nif.Parts.Count > 0 && nif.Parts.Any(part => part.Positions.Count > 0))
        {
            return true;
        }

        reason = "mesh has no geometry the editor reads: " + relative;
        return false;
    }

    static PlacedVersion VersionAt(SeenOverride version, NifMesh nif, string modelName, string modelPath)
    {
        var placed = Transform(nif, version.Ref!, version.Plugin, modelName);
        return new PlacedVersion
        {
            Plugin = version.Plugin,
            Disabled = false,
            ModelName = modelName,
            ModelPath = modelPath,
            Parts = placed.Parts,
            Placement = PlacementOf(version.Ref),
        };
    }

    static PlacementView PlacementOf(Pose pose)
    {
        return new PlacementView(pose.X, pose.Y, pose.Z, pose.Rx, pose.Ry, pose.Rz, pose.Scale);
    }

    static PlacementView? PlacementOf(IPlacedObjectGetter? refr)
    {
        if (refr?.Placement == null)
        {
            return null;
        }

        var place = refr.Placement;
        return new PlacementView(
            place.Position.X,
            place.Position.Y,
            place.Position.Z,
            place.Rotation.X,
            place.Rotation.Y,
            place.Rotation.Z,
            refr.Scale ?? 1f);
    }

    static string BaseNameOf(IReadOnlyList<SeenOverride> overrides, ILinkCache cache)
    {
        for (var i = overrides.Count - 1; i >= 0; i--)
        {
            var name = BaseNameOf(overrides[i].Ref, cache);
            if (name.Length > 0)
            {
                return name;
            }
        }

        return "";
    }

    static IMajorRecordGetter? BaseRecordOf(IReadOnlyList<SeenOverride> overrides, ILinkCache cache)
    {
        for (var i = overrides.Count - 1; i >= 0; i--)
        {
            if (overrides[i].Ref is { } refr && refr.Base.TryResolve(cache, out var record))
            {
                return record;
            }
        }

        return null;
    }

    static string SignatureOf(IMajorRecordGetter record) => record switch
    {
        IStaticGetter => "STAT",
        IMoveableStaticGetter => "MSTT",
        IFurnitureGetter => "FURN",
        IDoorGetter => "DOOR",
        IContainerGetter => "CONT",
        ITalkingActivatorGetter => "TACT",
        IActivatorGetter => "ACTI",
        ILightGetter => "LIGH",
        IMiscItemGetter => "MISC",
        IFloraGetter => "FLOR",
        ITreeGetter => "TREE",
        IBookGetter => "BOOK",
        IIngestibleGetter => "ALCH",
        IIngredientGetter => "INGR",
        IKeyGetter => "KEYM",
        IWeaponGetter => "WEAP",
        IArmorGetter => "ARMO",
        IAmmunitionGetter => "AMMO",
        ISoulGemGetter => "SLGM",
        IAlchemicalApparatusGetter => "APPA",
        IScrollGetter => "SCRL",
        IIdleMarkerGetter => "IDLM",
        ISoundMarkerGetter => "SOUN",
        IAddonNodeGetter => "ADDN",
        IGrassGetter => "GRAS",
        ITextureSetGetter => "TXST",
        _ => record.GetType().Name,
    };

    static IReadOnlyList<string> KeywordsOf(IMajorRecordGetter record, ILinkCache cache)
    {
        if (record is not IKeywordedGetter<IKeywordGetter> keyworded || keyworded.Keywords == null)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var link in keyworded.Keywords)
        {
            if (link.TryResolve(cache, out var keyword) && !string.IsNullOrEmpty(keyword.EditorID))
            {
                names.Add(keyword.EditorID);
            }
        }

        return names;
    }

    static string BaseNameOf(IPlacedObjectGetter? refr, ILinkCache cache)
    {
        if (refr == null || !refr.Base.TryResolve(cache, out var record))
        {
            return "";
        }

        var editor = record.EditorID ?? "";
        if (record is not INamedGetter named || string.IsNullOrWhiteSpace(named.Name))
        {
            return editor;
        }

        var full = named.Name.Trim();
        return editor.Length == 0 ? full : editor + "  " + full;
    }

    /// <summary>
    /// Position, rotation, and scale from the last enabled override that actually stored them.
    /// A later plugin that only changes the base keeps the earlier coordinates.
    /// </summary>
    static Pose WinningPose(IReadOnlyList<SeenOverride> overrides)
    {
        float? scale = null;
        for (var i = overrides.Count - 1; i >= 0; i--)
        {
            var refr = overrides[i].Ref;
            if (overrides[i].Disabled || refr == null)
            {
                continue;
            }

            scale ??= refr.Scale;
            if (refr.Placement == null)
            {
                continue;
            }

            var place = refr.Placement;
            return new Pose(
                place.Position.X,
                place.Position.Y,
                place.Position.Z,
                place.Rotation.X,
                place.Rotation.Y,
                place.Rotation.Z,
                scale ?? refr.Scale ?? 1f);
        }

        return new Pose(0, 0, 0, 0, 0, 0, scale ?? 1f);
    }

    static string? ModelNameOf(IPlacedObjectGetter refr, ILinkCache cache)
    {
        if (!refr.Base.TryResolve(cache, out var baseRecord) || baseRecord is not IModeledGetter modeled)
        {
            return null;
        }

        var relative = ModelPath(modeled);
        return relative == null ? null : Path.GetFileNameWithoutExtension(relative);
    }

    static bool TryFindCell(ISkyrimModGetter mod, FormKey key, bool interior, out ICellGetter cell)
    {
        if (interior)
        {
            foreach (var block in mod.Cells)
            {
                foreach (var sub in block.SubBlocks)
                {
                    foreach (var candidate in sub.Cells)
                    {
                        if (candidate.FormKey == key)
                        {
                            cell = candidate;
                            return true;
                        }
                    }
                }
            }
        }
        else
        {
            foreach (var world in mod.Worldspaces)
            {
                foreach (var block in world.SubCells)
                {
                    foreach (var sub in block.Items)
                    {
                        foreach (var candidate in sub.Items)
                        {
                            if (candidate.FormKey == key)
                            {
                                cell = candidate;
                                return true;
                            }
                        }
                    }
                }
            }
        }

        cell = null!;
        return false;
    }

    /// <summary>
    /// State at game start. A reference with an enable parent copies the parent's state (or its opposite)
    /// and its own Initially Disabled flag does not count; the player is always enabled.
    /// </summary>
    static bool StartsDisabled(
        ISkyrimMajorRecordGetter refr,
        IReadOnlyDictionary<FormKey, List<SeenOverride>> history,
        ILinkCache cache,
        Dictionary<FormKey, bool> known,
        int depth = 0)
    {
        if (known.TryGetValue(refr.FormKey, out var done) && depth > 0)
        {
            return done;
        }

        var flags = refr.SkyrimMajorRecordFlags;
        var sunk = refr is IPlacedObjectGetter { Placement.Position.Z: <= -29000 };
        bool result;
        if (flags.HasFlag(SkyrimMajorRecord.SkyrimMajorRecordFlag.Deleted) || sunk)
        {
            result = true;
        }
        else if (EnableParentOf(refr) is { } parent && !parent.Reference.FormKey.IsNull && depth < 16
            && ParentState(parent.Reference.FormKey, history, cache, known, depth) is bool parentOff)
        {
            result = parent.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent) ? !parentOff : parentOff;
        }
        else
        {
            result = flags.HasFlag(SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled);
        }

        if (depth > 0)
        {
            known[refr.FormKey] = result;
        }

        return result;
    }

    static bool? ParentState(
        FormKey key,
        IReadOnlyDictionary<FormKey, List<SeenOverride>> history,
        ILinkCache cache,
        Dictionary<FormKey, bool> known,
        int depth)
    {
        if (key == PlayerRef)
        {
            return false;
        }

        if (known.TryGetValue(key, out var done))
        {
            return done;
        }

        if (history.TryGetValue(key, out var versions) && versions.LastOrDefault(version => version.Ref != null)?.Ref is { } local)
        {
            return StartsDisabled(local, history, cache, known, depth + 1);
        }

        if (cache.TryResolve<IPlacedObjectGetter>(key, out var placed))
        {
            return StartsDisabled(placed, history, cache, known, depth + 1);
        }

        if (cache.TryResolve<IPlacedNpcGetter>(key, out var npc))
        {
            return StartsDisabled(npc, history, cache, known, depth + 1);
        }

        return null;
    }

    static IEnableParentGetter? EnableParentOf(ISkyrimMajorRecordGetter refr) => refr switch
    {
        IPlacedObjectGetter placed => placed.EnableParent,
        IPlacedNpcGetter npc => npc.EnableParent,
        _ => null,
    };

    /// <summary>One line about the enable parent, for the properties panel.</summary>
    static string EnableParentText(IPlacedObjectGetter? refr)
    {
        if (refr?.EnableParent is not { } parent || parent.Reference.FormKey.IsNull)
        {
            return "";
        }

        var opposite = parent.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent);
        var who = parent.Reference.FormKey == PlayerRef ? "player" : parent.Reference.FormKey.ToString();
        return "Enable parent " + who + (opposite ? " (opposite)" : "");
    }

    static bool IsSunk(IPlacedObjectGetter refr)
    {
        var flags = refr.SkyrimMajorRecordFlags;
        if (flags.HasFlag(SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled)
            || flags.HasFlag(SkyrimMajorRecord.SkyrimMajorRecordFlag.Deleted))
        {
            return true;
        }

        return refr.Placement?.Position.Z <= -29000;
    }

    static bool IsMarkerPlacement(IPlacedObjectGetter? refr, ILinkCache cache)
    {
        if (refr == null || !refr.Base.TryResolve(cache, out var baseRecord))
        {
            return false;
        }

        var editorId = baseRecord.EditorID;
        if (!string.IsNullOrEmpty(editorId) && editorId.Contains("marker", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (baseRecord is IModeledGetter modeled)
        {
            var path = ModelPath(modeled);
            if (!string.IsNullOrEmpty(path) && path.Contains("marker", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    static string? ModelPath(IModeledGetter modeled)
    {
        var file = modeled.Model?.File.DataRelativePath.ToString();
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        var relative = file.Replace('/', '\\').TrimStart('\\');
        const string prefix = "meshes\\";
        if (relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            relative = relative[prefix.Length..];
        }

        return relative;
    }

    static string? ResolveMesh(IReadOnlyList<string> roots, string relative)
    {
        return MeshCache.Locate(roots, relative);
    }

    static PlacedMesh Transform(NifMesh nif, IPlacedObjectGetter refr, string plugin, string modelName)
    {
        var place = refr.Placement;
        var pose = place == null
            ? new Pose(0, 0, 0, 0, 0, 0, refr.Scale ?? 1f)
            : new Pose(
                place.Position.X,
                place.Position.Y,
                place.Position.Z,
                place.Rotation.X,
                place.Rotation.Y,
                place.Rotation.Z,
                refr.Scale ?? 1f);
        return TransformPose(nif, refr.FormKey.ID.ToString("X8"), plugin, modelName, pose);
    }

    static PlacedMesh TransformPose(NifMesh nif, string formId, string plugin, string modelName, Pose pose)
    {
        var matrix = Matrix4x4.CreateScale(pose.Scale)
            * NifReader.PlacementRotation(pose.Rx, pose.Ry, pose.Rz)
            * Matrix4x4.CreateTranslation(pose.X, pose.Y, pose.Z);
        var parts = new List<PlacedPart>(nif.Parts.Count);
        foreach (var part in nif.Parts)
        {
            var positions = new List<Vector3>(part.Positions.Count);
            foreach (var point in part.Positions)
            {
                positions.Add(NifReader.ToView(Vector3.Transform(point, matrix)));
            }

            var normals = new List<Vector3>(part.Normals.Count);
            foreach (var normal in part.Normals)
            {
                var turned = NifReader.ToView(Vector3.TransformNormal(normal, matrix));
                normals.Add(turned.LengthSquared() < 1e-8f ? Vector3.Zero : Vector3.Normalize(turned));
            }

            parts.Add(new PlacedPart
            {
                Positions = positions,
                TriangleIndices = part.TriangleIndices,
                Uvs = part.Uvs,
                Normals = normals,
                DoubleSided = part.DoubleSided,
                Diffuse = part.Diffuse,
                NormalMap = part.NormalMap,
            });
        }

        return new PlacedMesh
        {
            FormId = formId,
            Plugin = plugin,
            ModelName = modelName,
            Parts = parts,
            Versions = [],
        };
    }

    void DisposeMods()
    {
        List<ISkyrimModGetter> old;
        lock (gate)
        {
            old = mods.ToList();
            mods.Clear();
            cells.Clear();
            models.Clear();
            fallbacks.Clear();
            linkCache = null;
        }

        foreach (var mod in old)
        {
            (mod as IDisposable)?.Dispose();
        }
    }
}

readonly record struct Pose(float X, float Y, float Z, float Rx, float Ry, float Rz, float Scale);

/// <summary>A reference placement. Position is Skyrim units. Rotation is radians, the same order the game stores.</summary>
public readonly record struct PlacementView(float X, float Y, float Z, float Rx, float Ry, float Rz, float Scale);

sealed class SeenOverride
{
    public required string Plugin { get; init; }

    public IPlacedObjectGetter? Ref { get; init; }

    public bool Disabled { get; set; }
}

static class LoadOrderList
{
    public static LoadOrder<ISkyrimModGetter> ToLoadOrder(this IReadOnlyList<ISkyrimModGetter> mods)
    {
        var order = new LoadOrder<ISkyrimModGetter>();
        foreach (var mod in mods)
        {
            order.Add(mod);
        }

        return order;
    }
}
