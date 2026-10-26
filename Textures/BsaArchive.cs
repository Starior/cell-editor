using System.IO;
using System.IO.Compression;
using K4os.Compression.LZ4.Streams;

namespace CellEditor.Textures;

public sealed class BsaArchive
{
    readonly string path;
    readonly bool embedNames;
    readonly Dictionary<string, Entry> files;

    BsaArchive(string path, bool embedNames, Dictionary<string, Entry> files)
    {
        this.path = path;
        this.embedNames = embedNames;
        this.files = files;
    }

    public static BsaArchive? TryOpen(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[36];
            stream.ReadExactly(header);
            if (header[0] != (byte)'B' || header[1] != (byte)'S' || header[2] != (byte)'A')
            {
                return null;
            }

            var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            var folderCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            var fileCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            var totalFileNameLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
            if (version is not (104 or 105) || folderCount > 100_000 || fileCount > 2_000_000)
            {
                return null;
            }

            var archiveCompressed = (flags & 0x4) != 0;
            var includeNames = (flags & 0x2) != 0;
            var record = version == 105 ? 24 : 16;
            var folderBytes = new byte[folderCount * record];
            stream.ReadExactly(folderBytes);
            var folders = new List<Folder>((int)folderCount);
            for (var i = 0; i < folderCount; i++)
            {
                var at = i * record;
                var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(folderBytes.AsSpan(at + 8));
                var offsetAt = version == 105 ? at + 16 : at + 12;
                var offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(folderBytes.AsSpan(offsetAt));
                var dataOffset = includeNames ? offset - totalFileNameLength : offset;
                folders.Add(new Folder(count, dataOffset));
            }

            var pending = new List<(string Folder, Entry Entry)>((int)fileCount);
            long nameAt = 0;
            foreach (var folder in folders)
            {
                stream.Position = folder.Offset;
                var nameLength = stream.ReadByte();
                if (nameLength < 0)
                {
                    return null;
                }

                var nameBytes = new byte[nameLength];
                stream.ReadExactly(nameBytes);
                var folderName = System.Text.Encoding.Latin1.GetString(nameBytes).TrimEnd('\0');
                var fileBytes = new byte[folder.Count * 16];
                stream.ReadExactly(fileBytes);
                for (var i = 0; i < folder.Count; i++)
                {
                    var rawSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(i * 16 + 8));
                    var data = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(i * 16 + 12));
                    var inverted = (rawSize & 0x40000000) != 0;
                    var size = rawSize & 0x3FFFFFFF;
                    pending.Add((folderName, new Entry(data, size, archiveCompressed ^ inverted)));
                }

                if (stream.Position > nameAt)
                {
                    nameAt = stream.Position;
                }
            }

            stream.Position = nameAt;
            var nameBlob = new byte[totalFileNameLength];
            stream.ReadExactly(nameBlob);
            var names = System.Text.Encoding.Latin1.GetString(nameBlob).Split('\0');
            var map = new Dictionary<string, Entry>(pending.Count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < pending.Count && i < names.Length; i++)
            {
                if (names[i].Length == 0)
                {
                    continue;
                }

                var relative = pending[i].Folder.Length == 0 ? names[i] : pending[i].Folder + "\\" + names[i];
                map[Normalize(relative)] = pending[i].Entry;
            }

            return new BsaArchive(path, (flags & 0x100) != 0, map);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool Contains(string relative)
    {
        return files.ContainsKey(Normalize(relative));
    }

    public byte[]? Read(string relative)
    {
        if (!files.TryGetValue(Normalize(relative), out var entry) || entry.Size > int.MaxValue)
        {
            return null;
        }

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = entry.Offset;
        var left = (int)entry.Size;
        if (embedNames)
        {
            var length = stream.ReadByte();
            if (length < 0 || 1 + length > left)
            {
                return null;
            }

            stream.Seek(length, SeekOrigin.Current);
            left -= 1 + length;
        }

        var payload = new byte[left];
        stream.ReadExactly(payload);
        if (!entry.Compressed)
        {
            return payload;
        }

        if (payload.Length <= 4)
        {
            return null;
        }

        var body = payload.AsSpan(4).ToArray();
        try
        {
            if (body.Length >= 4 && body[0] == 0x04 && body[1] == 0x22 && body[2] == 0x4D && body[3] == 0x18)
            {
                using var input = new MemoryStream(body);
                using var decoded = LZ4Stream.Decode(input);
                using var output = new MemoryStream();
                decoded.CopyTo(output);
                return output.ToArray();
            }

            using var zinput = new MemoryStream(body);
            using var zlib = new ZLibStream(zinput, CompressionMode.Decompress);
            using var zoutput = new MemoryStream();
            zlib.CopyTo(zoutput);
            return zoutput.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string Normalize(string relative)
    {
        return relative.Replace('/', '\\').TrimStart('\\');
    }

    readonly record struct Folder(uint Count, long Offset);

    readonly record struct Entry(long Offset, uint Size, bool Compressed);
}
