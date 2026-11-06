using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SnowPakTool.Desktop;

/// <summary>Replaces scene bytes without rebuilding the other PAK records.</summary>
internal static class MapArchiveWriter
{
    internal static void Write(string source, string destination, byte[] scene)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Map output must be separate from its input.");
        TruckReplacementStore.ValidateScene(scene);
        using var input = File.OpenRead(source);
        // Game archives can have padding after the ZIP end record.
        var tail = new byte[(int)Math.Min(input.Length, 1024 * 1024)];
        input.Position = input.Length - tail.Length; input.ReadExactly(tail);
        var endIndex = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) <= tail.Length) { endIndex = i; break; }
        if (endIndex < 0) throw new InvalidDataException("The map archive has no ZIP end record.");
        var end = tail.AsSpan(endIndex, 22).ToArray();
        var endPosition = input.Length - tail.Length + endIndex;
        var directoryOffset = U32(end, 16); var directorySize = U32(end, 12); var count = U16(end, 10);
        if (U16(end, 4) != 0 || U16(end, 6) != 0 || U16(end, 8) != count || count == ushort.MaxValue
            || directoryOffset == uint.MaxValue || directorySize > int.MaxValue || (long)directoryOffset + directorySize != endPosition)
            throw new InvalidDataException("Unsupported multi-volume or ZIP64 map archive.");
        var directory = new byte[(int)directorySize]; input.Position = directoryOffset; input.ReadExactly(directory);
        var records = new List<int>(); var target = -1; var cursor = 0;
        for (var i = 0; i < count; i++)
        {
            if (cursor + 46 > directory.Length || U32(directory, cursor) != 0x02014b50) throw new InvalidDataException("Invalid map archive directory.");
            var size = 46 + U16(directory, cursor + 28) + U16(directory, cursor + 30) + U16(directory, cursor + 32);
            if (cursor + size > directory.Length || U16(directory, cursor + 34) != 0 || U32(directory, cursor + 42) == uint.MaxValue)
                throw new InvalidDataException("Unsupported map archive entry.");
            var name = Encoding.UTF8.GetString(directory, cursor + 46, U16(directory, cursor + 28)).Replace('\\', '/');
            if (name.Equals("[scene]/data_for_ssl.bin", StringComparison.OrdinalIgnoreCase))
            {
                if (target >= 0) throw new InvalidDataException("Duplicate map scene entries.");
                target = cursor;
            }
            records.Add(cursor); cursor += size;
        }
        if (cursor != directory.Length || target < 0) throw new InvalidDataException("Expected one scene entry in the map archive.");
        var localOffset = U32(directory, target + 42); var method = U16(directory, target + 10);
        var flags = U16(directory, target + 8); var oldSize = U32(directory, target + 20);
        if ((flags & 9) != 0 || method is not (0 or 8) || oldSize == uint.MaxValue || U32(directory, target + 24) == uint.MaxValue)
            throw new InvalidDataException("Unsupported encrypted, streamed or ZIP64 scene entry.");
        input.Position = localOffset; var local = new byte[30]; input.ReadExactly(local);
        if (U32(local, 0) != 0x04034b50 || U16(local, 6) != flags || U16(local, 8) != method
            || U32(local, 14) != U32(directory, target + 16) || U32(local, 18) != oldSize || U32(local, 22) != U32(directory, target + 24))
            throw new InvalidDataException("The scene ZIP headers disagree.");
        var nameExtra = new byte[U16(local, 26) + U16(local, 28)]; input.ReadExactly(nameExtra);
        if (!nameExtra.AsSpan(0, U16(local, 26)).SequenceEqual(directory.AsSpan(target + 46, U16(directory, target + 28))))
            throw new InvalidDataException("The scene ZIP names disagree.");
        var dataOffset = input.Position; var oldEnd = checked(dataOffset + oldSize);
        if (oldEnd > directoryOffset || records.Any(record => U32(directory, record + 42) > localOffset && U32(directory, record + 42) < oldEnd))
            throw new InvalidDataException("Overlapping scene ZIP records.");
        byte[] payload = scene;
        if (method == 8)
        {
            using var compressed = new MemoryStream();
            using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(scene);
            payload = compressed.ToArray();
        }
        var crc = Crc(scene); var delta = (long)payload.Length - oldSize;
        static uint Zip32(long value) => value is >= 0 and < uint.MaxValue ? (uint)value : throw new InvalidDataException("The modified map requires ZIP64.");
        Put(local, 14, crc); Put(local, 18, (uint)payload.Length); Put(local, 22, (uint)scene.Length);
        Put(directory, target + 16, crc); Put(directory, target + 20, (uint)payload.Length); Put(directory, target + 24, (uint)scene.Length);
        foreach (var record in records)
        {
            var offset = U32(directory, record + 42);
            if (offset > localOffset) Put(directory, record + 42, Zip32(offset + delta));
        }
        Put(end, 16, Zip32(directoryOffset + delta));
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
        input.Position = 0; Copy(input, output, localOffset);
        output.Write(local); output.Write(nameExtra); output.Write(payload);
        input.Position = oldEnd; Copy(input, output, directoryOffset - oldEnd);
        output.Write(directory); output.Write(end);
        input.Position = endPosition + 22; Copy(input, output, input.Length - input.Position);
    }

    private static void Copy(Stream input, Stream output, long count)
    {
        var buffer = new byte[128 * 1024];
        while (count > 0)
        {
            var length = (int)Math.Min(count, buffer.Length); input.ReadExactly(buffer.AsSpan(0, length));
            output.Write(buffer, 0, length); count -= length;
        }
    }

    private static uint Crc(byte[] bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        return ~crc;
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
