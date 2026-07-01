using System.IO;
using System;
using System.Buffers.Binary;
using System.IO.Compression;

namespace Common;

/// <summary>
/// Minimal dependency-free PNG encoder: 8-bit RGBA, no interlacing, no filtering.
/// Input must be tightly packed rows (width * 4 bytes each) — strip any
/// BytesPerRow padding from GPU readbacks before calling.
/// </summary>
public static class PngWriter
{
    public static void Write(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException(
                $"Expected {width}x{height}x4 = {width * height * 4} bytes, got {rgba.Length}. " +
                "Rows must be tightly packed (no BytesPerRow padding).");
        }

        // PNG = 8-byte signature + chunks: IHDR (metadata), IDAT (zlib-compressed
        // scanlines, each prefixed by a filter byte), IEND (terminator).
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type: RGBA
        ihdr[10] = 0;  // compression: deflate (the only option)
        ihdr[11] = 0;  // filter method
        ihdr[12] = 0;  // no interlacing
        WriteChunk(fs, "IHDR"u8, ihdr);

        // Scanlines: filter byte 0 ("None") + raw row, then zlib the lot.
        var raw = new byte[height * (1 + width * 4)];
        for (int y = 0; y < height; y++)
        {
            raw[y * (1 + width * 4)] = 0;
            rgba.Slice(y * width * 4, width * 4).CopyTo(raw.AsSpan(y * (1 + width * 4) + 1));
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }
        WriteChunk(fs, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));

        WriteChunk(fs, "IEND"u8, default);
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(type);
        s.Write(data);
        // CRC32 over type + data, big-endian
        uint crc = Crc32(Crc32(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }
}
