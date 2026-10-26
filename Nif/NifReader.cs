using System.IO;
using System.Numerics;
using System.Text;

namespace CellEditor.Nif;

public sealed class NifPart
{
    public required IReadOnlyList<Vector3> Positions { get; init; }

    public required IReadOnlyList<int> TriangleIndices { get; init; }

    public required IReadOnlyList<Vector2> Uvs { get; init; }

    public string? Diffuse { get; init; }

    /// <summary>Second slot of the texture set: the tangent-space normal map.</summary>
    public string? NormalMap { get; init; }

    public Vector2 UvScale { get; init; } = Vector2.One;

    public Vector2 UvOffset { get; init; }

    public IReadOnlyList<Vector3> Normals { get; init; } = [];

    /// <summary>Shader flag SLSF2_Double_Sided. The texture is drawn on both sides of each triangle.</summary>
    public bool DoubleSided { get; init; }
}

public sealed class NifMesh
{
    public required IReadOnlyList<Vector3> Positions { get; init; }

    public required IReadOnlyList<int> TriangleIndices { get; init; }

    public required IReadOnlyList<NifPart> Parts { get; init; }

    public int ShapeCount { get; init; }
}

public sealed class NifFormatException : Exception
{
    public NifFormatException(string message) : base(message)
    {
    }
}

/// <summary>
/// Reads triangles out of a Skyrim SE NIF. Static BSTriShape blocks only.
/// </summary>
public static class NifReader
{
    const uint GamebryoVersion = 0x14020007;
    const uint SkyrimSeStream = 100;
    const uint SkyrimLeStream = 83;

    static readonly HashSet<string> NodeTypes = new(StringComparer.Ordinal)
    {
        "NiNode",
        "BSFadeNode",
        "BSLeafAnimNode",
        "BSValueNode",
        "NiBillboardNode",
        "BSDamageStage",
        "BSBlastNode",
        "BSDebrisNode",
        "BSRangeNode",
        "BSOrderedNode",
        "BSMultiBoundNode",
        "BSTreeNode",
    };

    static readonly HashSet<string> ShapeTypes = new(StringComparer.Ordinal)
    {
        "BSTriShape",
        "BSSubIndexTriShape",
        "BSMeshLODTriShape",
    };

    public static NifMesh Read(string path, bool toViewSpace = true)
    {
        var data = File.ReadAllBytes(path);
        var reader = new Cursor(data);

        var headerLine = reader.ReadLine();
        if (!headerLine.StartsWith("Gamebryo File Format", StringComparison.Ordinal))
        {
            throw new NifFormatException("This file does not start like a NIF.");
        }

        var version = reader.U32();
        _ = reader.U8();
        _ = reader.U32();
        var blockCount = reader.U32();
        var stream = reader.U32();
        if (version != GamebryoVersion || (stream != SkyrimSeStream && stream != SkyrimLeStream))
        {
            throw new NifFormatException("This viewer reads Skyrim SE and LE meshes. This file is a different NIF version (stream " + stream + ").");
        }

        _ = reader.ExportString();
        _ = reader.ExportString();
        _ = reader.ExportString();

        var typeCount = reader.U16();
        var types = new string[typeCount];
        for (var i = 0; i < typeCount; i++)
        {
            types[i] = reader.SizedString().TrimEnd('\0');
        }

        var typeIndex = new int[blockCount];
        for (var i = 0; i < blockCount; i++)
        {
            typeIndex[i] = reader.U16();
        }

        var sizes = new int[blockCount];
        var total = 0;
        for (var i = 0; i < blockCount; i++)
        {
            sizes[i] = checked((int)reader.U32());
            total += sizes[i];
        }

        var stringCount = reader.U32();
        _ = reader.U32();
        var strings = new string[stringCount];
        for (var i = 0; i < stringCount; i++)
        {
            strings[i] = reader.SizedString().TrimEnd('\0');
        }

        var groupCount = reader.U32();
        reader.Skip(checked((int)groupCount) * 4);

        var blockStart = reader.Offset;
        var blockEnd = blockStart + total;
        if (blockEnd > data.Length)
        {
            throw new NifFormatException("The NIF header does not match the file size.");
        }

        // After the blocks, Skyrim writes a footer: root count, then one block index per root.
        if (blockEnd < data.Length)
        {
            var footer = new Cursor(data, blockEnd);
            var rootCount = footer.U32();
            var footerEnd = footer.Offset + rootCount * 4;
            if (rootCount > 1024 || footerEnd != data.Length)
            {
                throw new NifFormatException("The NIF header does not match the file size.");
            }
        }

        var starts = new int[blockCount];
        var cursor = blockStart;
        for (var i = 0; i < blockCount; i++)
        {
            starts[i] = cursor;
            cursor += sizes[i];
        }

        var local = new Matrix4x4[blockCount];
        var children = new List<int>[blockCount];
        var shapes = new List<Shape>();
        for (var i = 0; i < blockCount; i++)
        {
            local[i] = Matrix4x4.Identity;
            var type = types[typeIndex[i]];
            if (NodeTypes.Contains(type))
            {
                var block = new Cursor(data, starts[i]);
                local[i] = ReadObjectTransform(block);
                var childCount = block.U32();
                children[i] = new List<int>(checked((int)childCount));
                for (var c = 0; c < childCount; c++)
                {
                    var child = block.I32();
                    if (child >= 0 && child < blockCount)
                    {
                        children[i].Add(child);
                    }
                }
            }
            else if (ShapeTypes.Contains(type))
            {
                var block = new Cursor(data, starts[i]);
                local[i] = ReadObjectTransform(block);
                var shape = ReadShape(block, i, out var skin);
                // A skinned SE shape keeps its vertices and triangles in the skin partition.
                if (shape.Positions.Count == 0 && skin >= 0 && skin < blockCount)
                {
                    shape = ReadSkinPartition(data, types, typeIndex, starts, skin, i, shape.Shader) ?? shape;
                }

                shapes.Add(shape);
            }
            else if (type == "NiTriShape")
            {
                var block = new Cursor(data, starts[i]);
                local[i] = ReadObjectTransform(block);
                if (ReadLegacyShape(data, types, typeIndex, starts, block, i) is { } shape)
                {
                    shapes.Add(shape);
                }
            }
        }

        var parent = new int[blockCount];
        Array.Fill(parent, -1);
        for (var i = 0; i < blockCount; i++)
        {
            if (children[i] == null)
            {
                continue;
            }

            foreach (var child in children[i])
            {
                parent[child] = i;
            }
        }

        var world = new Matrix4x4?[blockCount];
        Matrix4x4 World(int index)
        {
            if (world[index] is Matrix4x4 ready)
            {
                return ready;
            }

            // The game overwrites the root node's transform with the reference's placement,
            // so whatever turn the file stores on the root never shows in a cell.
            var result = parent[index] < 0 ? Matrix4x4.Identity : local[index] * World(parent[index]);
            world[index] = result;
            return result;
        }

        var positions = new List<Vector3>();
        var indices = new List<int>();
        var parts = new List<NifPart>();
        foreach (var shape in shapes)
        {
            if (shape.Positions.Count == 0 || shape.Indices.Count == 0)
            {
                continue;
            }

            var transform = World(shape.Block);
            var partPositions = new List<Vector3>(shape.Positions.Count);
            foreach (var position in shape.Positions)
            {
                var point = Vector3.Transform(position, transform);
                partPositions.Add(toViewSpace ? ToView(point) : point);
            }

            var partNormals = new List<Vector3>(shape.Normals.Count);
            foreach (var normal in shape.Normals)
            {
                var turned = Vector3.TransformNormal(normal, transform);
                if (toViewSpace)
                {
                    turned = ToView(turned);
                }

                partNormals.Add(turned.LengthSquared() < 1e-8f ? Vector3.Zero : Vector3.Normalize(turned));
            }

            var offset = positions.Count;
            positions.AddRange(partPositions);
            var partIndices = new List<int>(shape.Indices.Count);
            foreach (var index in shape.Indices)
            {
                partIndices.Add(index);
                indices.Add(offset + index);
            }

            var material = ReadMaterial(data, types, typeIndex, starts, shape.Shader);
            parts.Add(new NifPart
            {
                Positions = partPositions,
                TriangleIndices = partIndices,
                Uvs = ScaleUvs(shape.Uvs, material.Scale, material.Offset),
                Normals = partNormals,
                Diffuse = material.Path,
                NormalMap = material.NormalPath,
                UvScale = material.Scale,
                UvOffset = material.Offset,
                DoubleSided = material.DoubleSided,
            });
        }

        if (indices.Count == 0)
        {
            throw new NifFormatException("This NIF has no static mesh to show.");
        }

        return new NifMesh
        {
            Positions = positions,
            TriangleIndices = indices,
            Parts = parts,
            ShapeCount = shapes.Count,
        };
    }

    /// <summary>
    /// Reference rotation as the game applies it: positive angles turn clockwise, so the
    /// matrix is the transpose of the usual X-then-Y-then-Z rotation.
    /// </summary>
    public static Matrix4x4 PlacementRotation(float rx, float ry, float rz)
    {
        return Matrix4x4.CreateRotationZ(-rz)
            * Matrix4x4.CreateRotationY(-ry)
            * Matrix4x4.CreateRotationX(-rx);
    }

    public static Matrix4x4 PlacementRotationInverse(float rx, float ry, float rz)
    {
        return Matrix4x4.CreateRotationX(rx)
            * Matrix4x4.CreateRotationY(ry)
            * Matrix4x4.CreateRotationZ(rz);
    }

    static Matrix4x4 ReadObjectTransform(Cursor block)
    {
        _ = block.U32();
        var extraCount = block.U32();
        block.Skip(checked((int)extraCount) * 4);
        _ = block.I32();
        _ = block.U32();
        var tx = block.F32();
        var ty = block.F32();
        var tz = block.F32();
        var m11 = block.F32();
        var m12 = block.F32();
        var m13 = block.F32();
        var m21 = block.F32();
        var m22 = block.F32();
        var m23 = block.F32();
        var m31 = block.F32();
        var m32 = block.F32();
        var m33 = block.F32();
        var scale = block.F32();
        _ = block.I32();

        // Matrix33 is stored column by column (nif.xml), which is already the row-vector
        // layout System.Numerics multiplies with.
        var rotation = new Matrix4x4(
            m11, m12, m13, 0,
            m21, m22, m23, 0,
            m31, m32, m33, 0,
            0, 0, 0, 1);
        return Matrix4x4.CreateScale(scale) * rotation * Matrix4x4.CreateTranslation(tx, ty, tz);
    }

    static Shape ReadShape(Cursor block, int blockIndex, out int skin)
    {
        block.Skip(16);
        skin = block.I32();
        var shader = block.I32();
        _ = block.I32();
        var desc = block.U64();
        var triangleCount = block.U16();
        var vertexCount = block.U16();
        var dataSize = block.U32();
        if (dataSize == 0)
        {
            return new Shape(blockIndex, shader, [], [], [], []);
        }

        var stride = (int)(desc & 0xF) * 4;
        var expected = vertexCount * stride + triangleCount * 6;
        if (stride == 0 || expected != dataSize)
        {
            throw new NifFormatException("A mesh in this NIF has a vertex layout the viewer does not read yet.");
        }

        var (positions, uvs, normals) = ReadVertices(block, desc, stride, vertexCount);
        var indices = new List<int>(triangleCount * 3);
        for (var t = 0; t < triangleCount; t++)
        {
            indices.Add(block.U16());
            indices.Add(block.U16());
            indices.Add(block.U16());
        }

        return new Shape(blockIndex, shader, positions, indices, uvs, normals);
    }

    /// <summary>SE vertex records (BSVertexDataSSE): float position, half UV, byte normal at the offsets the descriptor gives.</summary>
    static (List<Vector3> Positions, List<Vector2> Uvs, List<Vector3> Normals) ReadVertices(Cursor block, ulong desc, int stride, int vertexCount)
    {
        var attributes = (desc >> 44) & 0xFFF;
        var uvOffset = (int)((desc >> 8) & 0xF) * 4;
        var normalOffset = (int)((desc >> 16) & 0xF) * 4;
        var hasUv = (attributes & 2) != 0 && uvOffset + 4 <= stride;
        var hasNormal = (attributes & 8) != 0 && normalOffset + 3 <= stride;
        if (vertexCount > 0 && ((attributes & 1) == 0 || stride < 12))
        {
            throw new NifFormatException("A mesh in this NIF stores vertices in a layout the viewer does not read yet.");
        }

        var positions = new List<Vector3>(vertexCount);
        var uvs = new List<Vector2>(vertexCount);
        var normals = new List<Vector3>(vertexCount);
        for (var v = 0; v < vertexCount; v++)
        {
            var raw = block.Bytes(stride);
            positions.Add(new Vector3(
                BitConverter.ToSingle(raw, 0),
                BitConverter.ToSingle(raw, 4),
                BitConverter.ToSingle(raw, 8)));
            if (!hasUv)
            {
                uvs.Add(default);
            }
            else
            {
                // Skyrim and this viewport are both D3D, so V is stored as the shader samples it.
                var u = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(raw, uvOffset));
                var vCoord = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(raw, uvOffset + 2));
                uvs.Add(new Vector2(u, vCoord));
            }

            if (!hasNormal)
            {
                normals.Add(Vector3.Zero);
                continue;
            }

            var nx = ByteUnit(raw[normalOffset]);
            var ny = ByteUnit(raw[normalOffset + 1]);
            var nz = ByteUnit(raw[normalOffset + 2]);
            var normal = new Vector3(nx, ny, nz);
            normals.Add(normal.LengthSquared() < 1e-8f ? Vector3.Zero : Vector3.Normalize(normal));
        }

        return (positions, uvs, normals);
    }

    /// <summary>NiSkinPartition in SE form: one shared vertex buffer, then per-partition triangles.</summary>
    static Shape? ReadSkinPartition(byte[] data, string[] types, int[] typeIndex, int[] starts, int skin, int blockIndex, int shader)
    {
        var skinType = types[typeIndex[skin]];
        if (skinType != "NiSkinInstance" && skinType != "BSDismemberSkinInstance")
        {
            return null;
        }

        var instance = new Cursor(data, starts[skin]);
        _ = instance.I32();
        var partitionBlock = instance.I32();
        if (partitionBlock < 0 || partitionBlock >= starts.Length || types[typeIndex[partitionBlock]] != "NiSkinPartition")
        {
            return null;
        }

        var block = new Cursor(data, starts[partitionBlock]);
        var partitionCount = block.U32();
        var dataSize = block.U32();
        var vertexSize = checked((int)block.U32());
        var desc = block.U64();
        if (dataSize == 0 || vertexSize == 0)
        {
            return null;
        }

        var vertexCount = checked((int)(dataSize / (uint)vertexSize));
        var (positions, uvs, normals) = ReadVertices(block, desc, vertexSize, vertexCount);
        var indices = new List<int>();
        for (var p = 0; p < partitionCount; p++)
        {
            var partVertices = block.U16();
            var triangleCount = block.U16();
            var boneCount = block.U16();
            var stripCount = block.U16();
            var weightsPerVertex = block.U16();
            block.Skip(boneCount * 2);
            ushort[]? map = null;
            if (block.U8() != 0)
            {
                map = new ushort[partVertices];
                for (var v = 0; v < partVertices; v++)
                {
                    map[v] = block.U16();
                }
            }

            if (block.U8() != 0)
            {
                block.Skip(partVertices * weightsPerVertex * 4);
            }

            var stripLengths = new int[stripCount];
            for (var s = 0; s < stripCount; s++)
            {
                stripLengths[s] = block.U16();
            }

            var triangles = new List<int>();
            if (block.U8() != 0)
            {
                if (stripCount == 0)
                {
                    for (var t = 0; t < triangleCount * 3; t++)
                    {
                        triangles.Add(block.U16());
                    }
                }
                else
                {
                    foreach (var length in stripLengths)
                    {
                        var strip = new int[length];
                        for (var s = 0; s < length; s++)
                        {
                            strip[s] = block.U16();
                        }

                        for (var s = 2; s < length; s++)
                        {
                            if (strip[s] == strip[s - 1] || strip[s] == strip[s - 2] || strip[s - 1] == strip[s - 2])
                            {
                                continue;
                            }

                            triangles.AddRange(s % 2 == 0
                                ? [strip[s - 2], strip[s - 1], strip[s]]
                                : [strip[s - 1], strip[s - 2], strip[s]]);
                        }
                    }
                }
            }

            if (block.U8() != 0)
            {
                block.Skip(partVertices * weightsPerVertex);
            }

            block.Skip(2 + 8 + triangleCount * 6);

            // Partition triangles index the partition's own vertices through the vertex map;
            // an index past the partition size means the file already stores buffer indices.
            var local = map != null && triangles.All(index => index < map.Length);
            for (var t = 0; t + 2 < triangles.Count; t += 3)
            {
                var a = local ? map![triangles[t]] : triangles[t];
                var b = local ? map![triangles[t + 1]] : triangles[t + 1];
                var c = local ? map![triangles[t + 2]] : triangles[t + 2];
                if (a < vertexCount && b < vertexCount && c < vertexCount)
                {
                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);
                }
            }
        }

        return new Shape(blockIndex, shader, positions, indices, uvs, normals);
    }

    /// <summary>Skyrim LE geometry: NiTriShape pointing at NiTriShapeData with float vertices.</summary>
    static Shape? ReadLegacyShape(byte[] data, string[] types, int[] typeIndex, int[] starts, Cursor block, int blockIndex)
    {
        var dataBlock = block.I32();
        _ = block.I32();
        var materials = block.U32();
        block.Skip(checked((int)materials) * 8);
        _ = block.I32();
        _ = block.U8();
        var shader = block.I32();
        _ = block.I32();
        if (dataBlock < 0 || dataBlock >= starts.Length || types[typeIndex[dataBlock]] != "NiTriShapeData")
        {
            return null;
        }

        var d = new Cursor(data, starts[dataBlock]);
        _ = d.I32();
        var count = d.U16();
        _ = d.U8();
        _ = d.U8();
        var positions = new List<Vector3>(count);
        if (d.U8() != 0)
        {
            for (var v = 0; v < count; v++)
            {
                positions.Add(new Vector3(d.F32(), d.F32(), d.F32()));
            }
        }

        var vectorFlags = d.U16();
        _ = d.U32();
        var normals = new List<Vector3>(count);
        var hasNormals = d.U8() != 0;
        if (hasNormals)
        {
            for (var v = 0; v < count; v++)
            {
                var normal = new Vector3(d.F32(), d.F32(), d.F32());
                normals.Add(normal.LengthSquared() < 1e-8f ? Vector3.Zero : Vector3.Normalize(normal));
            }

            if ((vectorFlags & 4096) != 0)
            {
                d.Skip(count * 24);
            }
        }

        d.Skip(16);
        if (d.U8() != 0)
        {
            d.Skip(count * 16);
        }

        var uvs = new List<Vector2>(count);
        if ((vectorFlags & 1) != 0)
        {
            for (var v = 0; v < count; v++)
            {
                uvs.Add(new Vector2(d.F32(), d.F32()));
            }
        }

        _ = d.U16();
        _ = d.I32();
        var triangleCount = d.U16();
        _ = d.U32();
        var indices = new List<int>(triangleCount * 3);
        if (d.U8() != 0)
        {
            for (var t = 0; t < triangleCount * 3; t++)
            {
                indices.Add(d.U16());
            }
        }

        if (positions.Count == 0 || indices.Any(index => index >= positions.Count))
        {
            return null;
        }

        while (normals.Count < positions.Count)
        {
            normals.Add(Vector3.Zero);
        }

        while (uvs.Count < positions.Count)
        {
            uvs.Add(default);
        }

        return new Shape(blockIndex, shader, positions, indices, uvs, normals);
    }

    static float ByteUnit(byte value)
    {
        return value / 255f * 2f - 1f;
    }

    static List<Vector2> ScaleUvs(List<Vector2> uvs, Vector2 scale, Vector2 offset)
    {
        if (scale == Vector2.One && offset == Vector2.Zero)
        {
            return uvs;
        }

        var scaled = new List<Vector2>(uvs.Count);
        foreach (var uv in uvs)
        {
            scaled.Add(new Vector2(uv.X * scale.X + offset.X, uv.Y * scale.Y + offset.Y));
        }

        return scaled;
    }

    readonly record struct MaterialInfo(string? Path, Vector2 Scale, Vector2 Offset, bool DoubleSided, string? NormalPath = null);

    static MaterialInfo ReadMaterial(byte[] data, string[] types, int[] typeIndex, int[] starts, int shader)
    {
        if (shader < 0 || shader >= starts.Length)
        {
            return new MaterialInfo(null, Vector2.One, Vector2.Zero, false);
        }

        var type = types[typeIndex[shader]];
        var block = new Cursor(data, starts[shader]);
        // On a lighting shader the shader-type enum is stored in front of the name.
        if (type == "BSLightingShaderProperty")
        {
            _ = block.U32();
        }

        _ = block.U32();
        var extraCount = block.U32();
        if (extraCount > 32)
        {
            return new MaterialInfo(null, Vector2.One, Vector2.Zero, false);
        }

        block.Skip(checked((int)extraCount) * 4);
        _ = block.I32();
        if (type == "BSEffectShaderProperty")
        {
            _ = block.U32();
            var effectFlags = block.U32();
            var effectOffset = new Vector2(block.F32(), block.F32());
            var effectScale = new Vector2(block.F32(), block.F32());
            return new MaterialInfo(CleanPath(block.SizedString()), effectScale, effectOffset, DoubleSided(effectFlags));
        }

        if (type != "BSLightingShaderProperty")
        {
            return new MaterialInfo(null, Vector2.One, Vector2.Zero, false);
        }

        _ = block.U32();
        var flags = block.U32();
        var uvOffset = new Vector2(block.F32(), block.F32());
        var uvScale = new Vector2(block.F32(), block.F32());
        var textureSet = block.I32();
        if (textureSet < 0 || textureSet >= starts.Length)
        {
            return new MaterialInfo(null, uvScale, uvOffset, DoubleSided(flags));
        }

        var textures = new Cursor(data, starts[textureSet]);
        var count = textures.U32();
        if (count == 0 || count > 16)
        {
            return new MaterialInfo(null, uvScale, uvOffset, DoubleSided(flags));
        }

        var diffuse = CleanPath(textures.SizedString());
        var normal = count >= 2 ? CleanPath(textures.SizedString()) : null;
        return new MaterialInfo(diffuse, uvScale, uvOffset, DoubleSided(flags), normal);
    }

    static bool DoubleSided(uint shaderFlags2)
    {
        return (shaderFlags2 & 0x10) != 0;
    }

    static string? CleanPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.Replace('/', '\\').TrimStart('\\');
    }

    /// <summary>
    /// Skyrim is Z-up. The viewport is Y-up.
    /// </summary>
    public static Vector3 ToView(Vector3 skyrim)
    {
        return new Vector3(skyrim.X, skyrim.Z, -skyrim.Y);
    }

    public static Vector3 FromView(Vector3 view)
    {
        return new Vector3(view.X, -view.Z, view.Y);
    }

    sealed class Shape(int block, int shader, List<Vector3> positions, List<int> indices, List<Vector2> uvs, List<Vector3> normals)
    {
        public int Block { get; } = block;

        public int Shader { get; } = shader;

        public List<Vector3> Positions { get; } = positions;

        public List<int> Indices { get; } = indices;

        public List<Vector2> Uvs { get; } = uvs;

        public List<Vector3> Normals { get; } = normals;
    }

    sealed class Cursor
    {
        readonly byte[] data;
        int offset;

        public Cursor(byte[] data, int offset = 0)
        {
            this.data = data;
            this.offset = offset;
        }

        public int Offset => offset;

        public string ReadLine()
        {
            var end = Array.IndexOf(data, (byte)'\n', offset);
            if (end < 0)
            {
                throw new NifFormatException("The NIF header is cut off.");
            }

            var line = Encoding.ASCII.GetString(data, offset, end - offset);
            offset = end + 1;
            return line;
        }

        public byte U8()
        {
            return data[offset++];
        }

        public ushort U16()
        {
            var value = BitConverter.ToUInt16(data, offset);
            offset += 2;
            return value;
        }

        public uint U32()
        {
            var value = BitConverter.ToUInt32(data, offset);
            offset += 4;
            return value;
        }

        public int I32()
        {
            var value = BitConverter.ToInt32(data, offset);
            offset += 4;
            return value;
        }

        public ulong U64()
        {
            var value = BitConverter.ToUInt64(data, offset);
            offset += 8;
            return value;
        }

        public float F32()
        {
            var value = BitConverter.ToSingle(data, offset);
            offset += 4;
            return value;
        }

        public void Skip(int count)
        {
            offset += count;
        }

        public byte[] Bytes(int count)
        {
            var bytes = new byte[count];
            Buffer.BlockCopy(data, offset, bytes, 0, count);
            offset += count;
            return bytes;
        }

        public string ExportString()
        {
            var length = data[offset++];
            var text = Encoding.Latin1.GetString(data, offset, length);
            offset += length;
            return text.TrimEnd('\0');
        }

        public string SizedString()
        {
            var length = checked((int)U32());
            var text = Encoding.Latin1.GetString(data, offset, length);
            offset += length;
            return text;
        }
    }
}
