using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace CellEditor.Cells;

/// <summary>One reference to override in a patch.</summary>
public sealed record PatchEdit(FormKey Reference, PlacementView Pose, bool WritePose, FormKey? Base, bool Disable);

public sealed partial class CellBrowser
{
    static readonly FormKey PlayerRef = FormKey.Factory("000014:Skyrim.esm");

    /// <summary>
    /// Writes overrides of the given references into a plugin. A new plugin is ESP-FE (ESL flag, HEDR 1.71):
    /// it only overrides records, so it never needs a full plugin's record range.
    /// The plugin is reopened afterwards so later saves see what it now holds.
    /// </summary>
    public int WritePatch(string path, bool create, IReadOnlyList<PatchEdit> edits)
    {
        List<ISkyrimModGetter> order;
        lock (gate)
        {
            if (mods.Count == 0)
            {
                throw new InvalidOperationException("No plugins are open.");
            }

            order = mods.ToList();
        }

        var fileName = Path.GetFileName(path);
        var modKey = ModKey.FromFileName(fileName);
        SkyrimMod patch;
        if (!create && File.Exists(path))
        {
            patch = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE);
        }
        else
        {
            patch = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
            patch.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
            patch.ModHeader.Stats.Version = 1.71f;
        }

        // One cache per plugin, newest first. A plugin Mutagen cannot parse is skipped instead of failing the save.
        var caches = order
            .Where(mod => mod.ModKey != modKey)
            .Reverse()
            .Select(mod => (mod.ModKey, (ILinkCache<ISkyrimMod, ISkyrimModGetter>)new[] { mod }.ToLoadOrder().ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>()))
            .ToList();
        skippedSources.Clear();
        foreach (var edit in edits)
        {
            var refr = patch.EnumerateMajorRecords<IPlacedObject>().FirstOrDefault(record => record.FormKey == edit.Reference);
            if (refr == null)
            {
                var context = FindSource(caches, edit.Reference)
                    ?? throw new InvalidOperationException(edit.Reference + " is not in the open plugins.");
                refr = context.GetOrAddAsOverride(patch);
            }

            if (edit.WritePose || edit.Disable)
            {
                refr.Placement ??= new Placement();
                var z = edit.Disable ? -30000f : edit.Pose.Z;
                refr.Placement.Position = new P3Float(edit.Pose.X, edit.Pose.Y, z);
                refr.Placement.Rotation = new P3Float(edit.Pose.Rx, edit.Pose.Ry, edit.Pose.Rz);
            }

            if (edit.Base is FormKey swap)
            {
                refr.Base.SetTo(swap);
            }

            if (edit.Disable)
            {
                refr.SkyrimMajorRecordFlags |= SkyrimMajorRecord.SkyrimMajorRecordFlag.InitiallyDisabled;
                var parent = new EnableParent { Flags = EnableParent.Flag.SetEnableStateToOppositeOfParent };
                parent.Reference.SetTo(PlayerRef);
                refr.EnableParent = parent;
            }
        }

        var folder = Path.GetDirectoryName(path)!;
        var tempFolder = Path.Combine(folder, ".cell-editor-tmp");
        Directory.CreateDirectory(tempFolder);
        var temp = Path.Combine(tempFolder, fileName);
        var listing = order.Select(mod => mod.ModKey).Where(key => key != modKey).Append(modKey).ToList();
        patch.BeginWrite
            .ToPath(temp)
            .WithLoadOrder(listing)
            .WithKnownMasters(order.Where(mod => mod.ModKey != modKey).Cast<IModMasterStyledGetter>().ToArray())
            .Write();
        ReplacePlugin(path, temp);
        try
        {
            Directory.Delete(tempFolder);
        }
        catch (IOException)
        {
        }

        return edits.Count;
    }

    readonly List<string> skippedSources = new();

    /// <summary>Plugins the last save could not read; their override of a reference was not used as the source.</summary>
    public IReadOnlyList<string> SkippedSources => skippedSources;

    Mutagen.Bethesda.Plugins.Cache.IModContext<ISkyrimMod, ISkyrimModGetter, IPlacedObject, IPlacedObjectGetter>? FindSource(
        IReadOnlyList<(ModKey Key, ILinkCache<ISkyrimMod, ISkyrimModGetter> Cache)> caches,
        FormKey reference)
    {
        foreach (var (key, cache) in caches)
        {
            try
            {
                if (cache.TryResolveContext<IPlacedObject, IPlacedObjectGetter>(reference, out var context))
                {
                    return context;
                }
            }
            catch (Exception)
            {
                var name = key.FileName.String;
                if (!skippedSources.Contains(name))
                {
                    skippedSources.Add(name);
                }
            }
        }

        return null;
    }

    /// <summary>Swaps the file under an open plugin and opens it again in the same load-order slot.</summary>
    void ReplacePlugin(string path, string temp)
    {
        var name = Path.GetFileName(path);
        lock (gate)
        {
            var index = mods.FindIndex(mod => mod.ModKey.FileName.String.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                (mods[index] as IDisposable)?.Dispose();
            }

            try
            {
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                var reopened = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                if (index >= 0)
                {
                    mods[index] = reopened;
                }
                else
                {
                    mods.Add(reopened);
                }

                linkCache = mods.ToLoadOrder().ToImmutableLinkCache();
            }
        }
    }
}
