using System.IO;
using System.Text;

namespace CellEditor.Plugins;

public static class PluginHeader
{
    public static string[] ReadMasters(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);
        var type = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (type != "TES4")
        {
            return [];
        }

        var dataSize = reader.ReadUInt32();
        stream.Seek(16, SeekOrigin.Current);
        var end = stream.Position + dataSize;
        var masters = new List<string>();
        while (stream.Position + 6 <= end)
        {
            var sub = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = reader.ReadUInt16();
            if (sub == "XXXX")
            {
                length = checked((ushort)reader.ReadUInt32());
                sub = Encoding.ASCII.GetString(reader.ReadBytes(4));
                _ = reader.ReadUInt16();
            }

            var data = reader.ReadBytes(length);
            if (sub != "MAST")
            {
                continue;
            }

            var zero = Array.IndexOf(data, (byte)0);
            var count = zero < 0 ? data.Length : zero;
            masters.Add(Encoding.Latin1.GetString(data, 0, count));
        }

        return masters.ToArray();
    }
}
