using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace server.core.Remediate.Rasterize;

public static class PngEncoder
{
    private static ReadOnlySpan<byte> PngSignature => new byte[]
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
    };

    public static byte[] EncodeBgra32(BgraBitmap bitmap)
    {
        if (!bitmap.IsValid)
        {
            throw new ArgumentException("Bitmap is invalid.", nameof(bitmap));
        }

        using var output = new MemoryStream(capacity: bitmap.WidthPx * bitmap.HeightPx);
        output.Write(PngSignature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)bitmap.WidthPx);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)bitmap.HeightPx);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // color type: RGBA
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(output, "IHDR", ihdr);

        var rgbaScanlines = BuildRgbaScanlines(bitmap);
        var compressed = CompressZlib(rgbaScanlines);
        WriteChunk(output, "IDAT", compressed);

        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static byte[] BuildRgbaScanlines(BgraBitmap bitmap)
    {
        var rowBytes = bitmap.WidthPx * 4;
        var scanlineBytes = 1 + rowBytes; // filter byte + pixels
        var raw = new byte[bitmap.HeightPx * scanlineBytes];

        for (var y = 0; y < bitmap.HeightPx; y++)
        {
            var srcRowOffset = y * bitmap.StrideBytes;
            var dstRowOffset = y * scanlineBytes;

            raw[dstRowOffset] = 0; // filter type: None

            var dst = raw.AsSpan(dstRowOffset + 1, rowBytes);
            var src = bitmap.BgraBytes.AsSpan(srcRowOffset, rowBytes);

            // Convert BGRA -> RGBA
            for (var i = 0; i < rowBytes; i += 4)
            {
                dst[i + 0] = src[i + 2]; // R
                dst[i + 1] = src[i + 1]; // G
                dst[i + 2] = src[i + 0]; // B
                dst[i + 3] = src[i + 3]; // A
            }
        }

        return raw;
    }

    private static byte[] CompressZlib(byte[] raw)
    {
        using var ms = new MemoryStream(capacity: raw.Length / 2);
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw, 0, raw.Length);
        }

        return ms.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        if (type.Length != 4)
        {
            throw new ArgumentException("PNG chunk type must be 4 characters.", nameof(type));
        }

        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)data.Length);
        output.Write(lenBytes);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes, 0, typeBytes.Length);

        if (!data.IsEmpty)
        {
            output.Write(data);
        }

        Span<byte> crcBytes = stackalloc byte[4];
        var crc = ComputeCrc(typeBytes, data);
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint ComputeCrc(byte[] typeBytes, ReadOnlySpan<byte> data)
    {
        var crc32 = new Crc32();
        crc32.Append(typeBytes);
        if (!data.IsEmpty)
        {
            crc32.Append(data);
        }

        Span<byte> hash = stackalloc byte[4];
        crc32.GetCurrentHash(hash);
        return BinaryPrimitives.ReadUInt32BigEndian(hash);
    }
}

