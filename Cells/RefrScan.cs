using System.IO;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace CellEditor.Cells;

public readonly record struct RawPlacement(bool Disabled, bool HasPosition, float Z);

public static class RefrScan
{
    const uint InitiallyDisabled = 0x800;
    const uint Deleted = 0x20;
    const uint Compressed = 0x40000;

    public static Dictionary<FormKey, RawPlacement> Read(ISkyrimModGetter mod, string path)
    {
        var masters = new List<ModKey>();
        foreach (var reference in mod.MasterReferences)
        {
            masters.Add(reference.Master);
        }

        var data = File.ReadAllBytes(path);
        var found = new Dictionary<FormKey, RawPlacement>();
        Walk(data, 0, data.Length, mod.ModKey, masters, found);
        return found;
    }

    static void Walk(byte[] data, int start, int end, ModKey self, List<ModKey> masters, Dictionary<FormKey, RawPlacement> found)
    {
        var offset = start;
        while (offset + 24 <= end)
        {
            var type = Tag(data, offset);
            var size = BitConverter.ToUInt32(data, offset + 4);
            if (type == "GRUP")
            {
                var groupEnd = offset + (int)size;
                if (groupEnd > end || size < 24)
                {
                    return;
                }

                Walk(data, offset + 24, groupEnd, self, masters, found);
                offset = groupEnd;
                continue;
            }

            var recordEnd = offset + 24 + (int)size;
            if (recordEnd > end)
            {
                return;
            }

            if (type == "REFR")
            {
                var flags = BitConverter.ToUInt32(data, offset + 8);
                var formId = BitConverter.ToUInt32(data, offset + 12);
                if ((flags & Compressed) == 0 && TryKey(self, masters, formId, out var key))
                {
                    var disabled = (flags & (InitiallyDisabled | Deleted)) != 0;
                    var z = 0f;
                    var hasZ = TryPositionZ(data, offset + 24, recordEnd, out z);
                    found[key] = new RawPlacement(disabled, hasZ, z);
                }
            }

            offset = recordEnd;
        }
    }

    static bool TryKey(ModKey self, List<ModKey> masters, uint formId, out FormKey key)
    {
        var index = (int)(formId >> 24);
        var id = formId & 0x00FFFFFF;
        if (id >= 0x00FFFFFF)
        {
            key = default;
            return false;
        }

        if (index == masters.Count)
        {
            key = new FormKey(self, id);
            return true;
        }

        if (index < 0 || index >= masters.Count)
        {
            key = default;
            return false;
        }

        key = new FormKey(masters[index], id);
        return true;
    }

    static bool TryPositionZ(byte[] data, int start, int end, out float z)
    {
        var offset = start;
        while (offset + 6 <= end)
        {
            var type = Tag(data, offset);
            var size = BitConverter.ToUInt16(data, offset + 4);
            var body = offset + 6;
            if (body + size > end)
            {
                break;
            }

            if (type == "DATA" && size >= 12)
            {
                z = BitConverter.ToSingle(data, body + 8);
                return true;
            }

            offset = body + size;
        }

        z = 0;
        return false;
    }

    static string Tag(byte[] data, int offset)
    {
        return System.Text.Encoding.ASCII.GetString(data, offset, 4);
    }
}
