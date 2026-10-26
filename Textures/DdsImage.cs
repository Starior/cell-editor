using System.IO;
using HelixToolkit.SharpDX;
using Pfim;
using DxFormat = SharpDX.DXGI.Format;

namespace CellEditor.Textures;

public static class DdsImage
{
    public readonly record struct Decoded(byte[] Pixels, int Width, int Height);

    public static TextureModel? Load(byte[] dds)
    {
        var decoded = Decode(dds);
        return decoded == null ? null : ToModel(decoded.Value);
    }

    public static Decoded? Decode(byte[] dds)
    {
        try
        {
            using var image = Pfimage.FromStream(new MemoryStream(dds));
            var pixels = ToRgba(image);
            if (pixels == null)
            {
                return null;
            }

            return new Decoded(pixels, image.Width, image.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static TextureModel ToModel(Decoded decoded)
    {
        return new TextureModel(decoded.Pixels, DxFormat.R8G8B8A8_UNorm, decoded.Width, decoded.Height);
    }

    static byte[]? ToRgba(IImage image)
    {
        var width = image.Width;
        var height = image.Height;
        var pixels = new byte[width * height * 4];
        var source = image.Data;
        // Pfim hands back BGRA / BGR bytes whatever the format name says.
        if (image.Format == ImageFormat.Rgba32)
        {
            CopyRows(source, image.Stride, pixels, width * 4, height);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }

            return pixels;
        }

        if (image.Format == ImageFormat.Rgb24)
        {
            for (var y = 0; y < height; y++)
            {
                var from = y * image.Stride;
                var to = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    pixels[to] = source[from + 2];
                    pixels[to + 1] = source[from + 1];
                    pixels[to + 2] = source[from];
                    pixels[to + 3] = 255;
                    from += 3;
                    to += 4;
                }
            }

            return pixels;
        }

        return null;
    }

    static void CopyRows(byte[] source, int stride, byte[] target, int rowBytes, int height)
    {
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(source, y * stride, target, y * rowBytes, rowBytes);
        }
    }
}
