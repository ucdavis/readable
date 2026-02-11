using System.Buffers.Binary;
using FluentAssertions;
using server.core.Remediate.Rasterize;

namespace server.tests.Unit.Remediate;

public sealed class PngEncoderTests
{
    [Fact]
    public void EncodeBgra32_WritesValidPngContainerChunks()
    {
        // 2x2 solid opaque white pixels in BGRA order.
        var bytes = new byte[2 * 2 * 4];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i + 0] = 255; // B
            bytes[i + 1] = 255; // G
            bytes[i + 2] = 255; // R
            bytes[i + 3] = 255; // A
        }

        var bitmap = new BgraBitmap(bytes, WidthPx: 2, HeightPx: 2, StrideBytes: 2 * 4);
        var png = PngEncoder.EncodeBgra32(bitmap);

        png.Length.Should().BeGreaterThan(8);
        png.AsSpan(0, 8).ToArray().Should().Equal(
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);

        var offset = 8;
        var chunkTypes = new List<string>();
        uint? iendCrc = null;
        int safety = 0;

        while (offset < png.Length && safety++ < 100)
        {
            (uint length, string type, int dataStart, int dataEndExclusive, uint crc, int nextOffset) = ReadChunk(png, offset);
            chunkTypes.Add(type);

            if (type == "IEND")
            {
                length.Should().Be(0);
                iendCrc = crc;
                nextOffset.Should().Be(png.Length);
                break;
            }

            offset = nextOffset;
        }

        chunkTypes[0].Should().Be("IHDR");
        chunkTypes.Should().Contain("IDAT");
        chunkTypes[^1].Should().Be("IEND");

        // PNG spec: CRC of IEND with no data is always AE426082.
        iendCrc.Should().Be(0xAE426082);
    }

    private static (uint Length, string Type, int DataStart, int DataEndExclusive, uint Crc, int NextOffset) ReadChunk(byte[] png, int offset)
    {
        if (offset + 12 > png.Length)
        {
            throw new InvalidOperationException("PNG chunk truncated.");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
        var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
        var dataStart = offset + 8;
        var dataEndExclusive = dataStart + checked((int)length);

        if (dataEndExclusive + 4 > png.Length)
        {
            throw new InvalidOperationException("PNG chunk data truncated.");
        }

        var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(dataEndExclusive, 4));
        var nextOffset = dataEndExclusive + 4;
        return (length, type, dataStart, dataEndExclusive, crc, nextOffset);
    }
}

