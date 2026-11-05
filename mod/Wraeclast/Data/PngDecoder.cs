using System;
using System.IO;
using System.IO.Compression;

namespace Wraeclast.Data;

/// <summary>
/// PNG reader for the mod's own art (class portraits and the base colour
/// texture inside each GLB). The game's <c>ImageConversion.LoadImage</c> cannot
/// be used here: the IL2CPP binding builds a temporary array wrapper that the
/// runtime collects mid-call (<c>ObjectCollectedException</c>, and one access
/// violation), so textures are decoded to raw RGBA and uploaded through
/// <c>Texture2D.LoadRawTextureData(IntPtr, int)</c> instead.
///
/// Supported: 8 and 16 bit, greyscale, truecolour, palette, greyscale+alpha and
/// truecolour+alpha, non-interlaced. Everything PoE ships is in that set.
/// </summary>
internal static class PngDecoder
{
    internal sealed class Image
    {
        internal int Width;
        internal int Height;
        internal byte[] Rgba;
    }

    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    internal static Image Decode(byte[] png)
    {
        for (var index = 0; index < Signature.Length; index++)
        {
            if (png[index] != Signature[index])
            {
                throw new InvalidDataException("not a PNG file");
            }
        }

        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = 0;
        var interlace = 0;
        byte[] palette = null;
        byte[] paletteAlpha = null;
        var compressed = new MemoryStream();

        var offset = 8;
        while (offset + 8 <= png.Length)
        {
            var length = (int)ReadUInt32BigEndian(png, offset);
            var type = ReadType(png, offset + 4);
            offset += 8;
            if (offset + length > png.Length)
            {
                break;
            }

            if (type == "IHDR")
            {
                width = (int)ReadUInt32BigEndian(png, offset);
                height = (int)ReadUInt32BigEndian(png, offset + 4);
                bitDepth = png[offset + 8];
                colorType = png[offset + 9];
                interlace = png[offset + 12];
            }
            else if (type == "PLTE")
            {
                palette = new byte[length];
                Array.Copy(png, offset, palette, 0, length);
            }
            else if (type == "tRNS")
            {
                paletteAlpha = new byte[length];
                Array.Copy(png, offset, paletteAlpha, 0, length);
            }
            else if (type == "IDAT")
            {
                compressed.Write(png, offset, length);
            }
            else if (type == "IEND")
            {
                break;
            }

            offset += length + 4;
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("PNG header missing");
        }

        if (interlace != 0)
        {
            throw new InvalidDataException("interlaced PNG is not supported");
        }

        if (bitDepth != 8 && bitDepth != 16)
        {
            throw new InvalidDataException($"unsupported PNG bit depth {bitDepth}");
        }

        var channels = Channels(colorType);
        var bytesPerPixel = channels * (bitDepth / 8);
        var stride = width * bytesPerPixel;
        var raw = Inflate(compressed, height * (stride + 1));
        var pixels = Unfilter(raw, height, stride, bytesPerPixel);

        var image = new Image { Width = width, Height = height, Rgba = new byte[width * height * 4] };
        var sourceStep = bitDepth == 16 ? 2 : 1;
        var source = 0;

        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var target = pixel * 4;
            switch (colorType)
            {
                case 0:
                    image.Rgba[target] = pixels[source];
                    image.Rgba[target + 1] = pixels[source];
                    image.Rgba[target + 2] = pixels[source];
                    image.Rgba[target + 3] = 255;
                    break;

                case 2:
                    image.Rgba[target] = pixels[source];
                    image.Rgba[target + 1] = pixels[source + sourceStep];
                    image.Rgba[target + 2] = pixels[source + sourceStep * 2];
                    image.Rgba[target + 3] = 255;
                    break;

                case 3:
                    var index = pixels[source];
                    var paletteOffset = index * 3;
                    image.Rgba[target] = palette[paletteOffset];
                    image.Rgba[target + 1] = palette[paletteOffset + 1];
                    image.Rgba[target + 2] = palette[paletteOffset + 2];
                    image.Rgba[target + 3] =
                        paletteAlpha != null && index < paletteAlpha.Length ? paletteAlpha[index] : (byte)255;
                    break;

                case 4:
                    image.Rgba[target] = pixels[source];
                    image.Rgba[target + 1] = pixels[source];
                    image.Rgba[target + 2] = pixels[source];
                    image.Rgba[target + 3] = pixels[source + sourceStep];
                    break;

                default:
                    image.Rgba[target] = pixels[source];
                    image.Rgba[target + 1] = pixels[source + sourceStep];
                    image.Rgba[target + 2] = pixels[source + sourceStep * 2];
                    image.Rgba[target + 3] = pixels[source + sourceStep * 3];
                    break;
            }

            source += bytesPerPixel;
        }

        return image;
    }

    private static byte[] Inflate(MemoryStream compressed, int expected)
    {
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var output = new byte[expected];
        var read = 0;
        while (read < expected)
        {
            var chunk = zlib.Read(output, read, expected - read);
            if (chunk <= 0)
            {
                break;
            }

            read += chunk;
        }

        if (read != expected)
        {
            throw new InvalidDataException($"PNG data truncated ({read}/{expected} bytes)");
        }

        return output;
    }

    private static byte[] Unfilter(byte[] raw, int height, int stride, int bytesPerPixel)
    {
        // Each scanline is prefixed by its filter byte; the output drops those
        // so the conversion loop sees a compact RGBA-style buffer.
        var pixels = new byte[height * stride];

        for (var row = 0; row < height; row++)
        {
            var rowStart = row * (stride + 1);
            var filter = raw[rowStart];
            var source = rowStart + 1;
            var target = row * stride;
            var previous = target - stride;

            for (var index = 0; index < stride; index++)
            {
                var value = raw[source + index];
                byte left = index >= bytesPerPixel ? pixels[target + index - bytesPerPixel] : (byte)0;
                byte up = row > 0 ? pixels[previous + index] : (byte)0;
                byte upLeft = row > 0 && index >= bytesPerPixel ? pixels[previous + index - bytesPerPixel] : (byte)0;

                switch (filter)
                {
                    case 0:
                        break;
                    case 1:
                        value += left;
                        break;
                    case 2:
                        value += up;
                        break;
                    case 3:
                        value += (byte)((left + up) / 2);
                        break;
                    case 4:
                        value += Paeth(left, up, upLeft);
                        break;
                    default:
                        throw new InvalidDataException($"unknown PNG filter {filter}");
                }

                pixels[target + index] = value;
            }
        }

        return pixels;
    }

    private static byte Paeth(byte left, byte up, byte upLeft)
    {
        var estimate = left + up - upLeft;
        var da = Math.Abs(estimate - left);
        var db = Math.Abs(estimate - up);
        var dc = Math.Abs(estimate - upLeft);

        if (da <= db && da <= dc)
        {
            return left;
        }

        return db <= dc ? up : upLeft;
    }

    private static int Channels(int colorType)
    {
        switch (colorType)
        {
            case 0:
                return 1;
            case 2:
                return 3;
            case 3:
                return 1;
            case 4:
                return 2;
            case 6:
                return 4;
            default:
                throw new InvalidDataException($"unsupported PNG colour type {colorType}");
        }
    }

    private static uint ReadUInt32BigEndian(byte[] data, int offset) =>
        (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

    private static string ReadType(byte[] data, int offset) =>
        System.Text.Encoding.ASCII.GetString(data, offset, 4);
}
