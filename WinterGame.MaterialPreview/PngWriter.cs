using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace WinterGame.MaterialPreview;

/// <summary>
///     Writes 8-bit grey or RGB PNG files with unfiltered rows.
/// </summary>
internal static class PngWriter
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>
    ///     Writes <paramref name="pixels"/>, which holds <paramref name="channels"/> bytes per pixel row after row: 1 for grey, 3 for RGB.
    /// </summary>
    public static void Write(string path, int width, int height, int channels, ReadOnlySpan<byte> pixels)
    {
        if (channels != 1 && channels != 3)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "A preview is grey or RGB.");
        }

        using var file = File.Create(path);
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = channels == 1 ? (byte)0 : (byte)2;
        WriteChunk(file, "IHDR", header);

        using var compressed = new MemoryStream();

        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true))
        {
            var rowBytes = width * channels;

            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(pixels.Slice(y * rowBytes, rowBytes));
            }
        }

        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        stream.Write(word);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(data, Crc32(typeBytes, 0xFFFFFFFFu)) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        stream.Write(word);
    }

    private static uint Crc32(byte[] data, uint crc)
    {
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];

        for (var n = 0u; n < 256; n++)
        {
            var c = n;

            for (var bit = 0; bit < 8; bit++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
