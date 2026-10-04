using System.IO.Compression;
using System.IO;
using System.Text;
using Promptarium.Models;

namespace Promptarium.Services;

public sealed class PngMetadataReader
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private const int MaxTextChunkBytes = 8 * 1024 * 1024;
    private const long MaxTotalTextBytes = 32L * 1024 * 1024;
    private const int MaxChunks = 100_000;

    public PngMetadata Read(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var signature = reader.ReadBytes(8);
        if (!signature.SequenceEqual(PngSignature))
        {
            return new PngMetadata { IsPng = false };
        }

        var metadata = new PngMetadata { IsPng = true };
        long totalInputBytes = 0, totalTextBytes = 0;
        var chunkCount = 0;
        while (stream.Position + 12 <= stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++chunkCount > MaxChunks) throw new InvalidDataException("PNG chunk count exceeds the limit.");
            var length = ReadUInt32BigEndian(reader);
            var type = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (length > stream.Length - stream.Position - 4)
            {
                throw new InvalidDataException("PNG chunk length is invalid.");
            }

            if (type == "IEND" && length != 0) throw new InvalidDataException("PNG IEND length is invalid.");
            var isText = type is "tEXt" or "zTXt" or "iTXt";
            if (type != "IHDR" && !isText)
            {
                stream.Seek(length, SeekOrigin.Current);
                _ = reader.ReadBytes(4);
                if (type == "IEND") return metadata;
                continue;
            }
            if (type == "IHDR" && length != 13) throw new InvalidDataException("PNG IHDR length is invalid.");
            if (isText)
            {
                if (length > MaxTextChunkBytes || length > MaxTotalTextBytes - totalInputBytes)
                    throw new InvalidDataException("PNG text input exceeds the limit.");
                totalInputBytes += length;
            }
            var data = ReadData(stream, checked((int)length), cancellationToken);
            _ = reader.ReadBytes(4); // CRC is validated by the image decoder when rendering.

            switch (type)
            {
                case "IHDR" when data.Length >= 8:
                    metadata = new PngMetadata
                    {
                        IsPng = true,
                        Width = ReadInt32BigEndian(data, 0),
                        Height = ReadInt32BigEndian(data, 4)
                    };
                    break;
                case "tEXt":
                    AddTextChunk(metadata.Text, data, Encoding.Latin1, ref totalTextBytes);
                    break;
                case "zTXt":
                    AddCompressedTextChunk(metadata.Text, data, ref totalTextBytes, cancellationToken);
                    break;
                case "iTXt":
                    AddInternationalTextChunk(metadata.Text, data, ref totalTextBytes, cancellationToken);
                    break;
                case "IEND":
                    return metadata;
            }
        }

        throw new InvalidDataException("PNG is truncated or missing IEND.");
    }

    private static uint ReadUInt32BigEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4)
        {
            throw new EndOfStreamException();
        }

        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static int ReadInt32BigEndian(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    private static void AddTextChunk(IDictionary<string, string> destination, byte[] data, Encoding encoding, ref long totalTextBytes)
    {
        var separator = Array.IndexOf(data, (byte)0);
        if (separator <= 0)
        {
            return;
        }

        var key = Encoding.Latin1.GetString(data, 0, separator);
        AccountText(data.Length, ref totalTextBytes);
        var value = encoding.GetString(data, separator + 1, data.Length - separator - 1);
        destination[key] = value;
    }

    private static void AddCompressedTextChunk(IDictionary<string, string> destination, byte[] data, ref long totalTextBytes, CancellationToken cancellationToken)
    {
        var separator = Array.IndexOf(data, (byte)0);
        if (separator <= 0 || separator + 2 > data.Length)
        {
            return;
        }

        var key = Encoding.Latin1.GetString(data, 0, separator);
        if (data[separator + 1] != 0) throw new InvalidDataException("PNG text compression method is invalid.");
        AccountText(separator, ref totalTextBytes);
        var compressed = data[(separator + 2)..];
        destination[key] = InflateToString(compressed, Encoding.Latin1, ref totalTextBytes, cancellationToken);
    }

    private static void AddInternationalTextChunk(IDictionary<string, string> destination, byte[] data, ref long totalTextBytes, CancellationToken cancellationToken)
    {
        var index = Array.IndexOf(data, (byte)0);
        if (index <= 0 || index + 3 > data.Length)
        {
            return;
        }

        var key = Encoding.Latin1.GetString(data, 0, index);
        var compressionFlag = data[index + 1];
        if (compressionFlag > 1 || data[index + 2] != 0) throw new InvalidDataException("PNG text compression method is invalid.");
        var cursor = index + 3;
        cursor = SkipNullTerminated(data, cursor); // language tag
        cursor = SkipNullTerminated(data, cursor); // translated keyword
        if (cursor > data.Length)
        {
            return;
        }

        var textBytes = data[cursor..];
        AccountText(index, ref totalTextBytes);
        if (compressionFlag == 0) AccountText(textBytes.Length, ref totalTextBytes);
        destination[key] = compressionFlag == 1
            ? InflateToString(textBytes, Encoding.UTF8, ref totalTextBytes, cancellationToken)
            : Encoding.UTF8.GetString(textBytes);
    }

    private static int SkipNullTerminated(byte[] data, int start)
    {
        var end = Array.IndexOf(data, (byte)0, start);
        return end < 0 ? data.Length + 1 : end + 1;
    }

    private static string InflateToString(byte[] compressed, Encoding encoding, ref long totalTextBytes, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = zlib.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            if (count > MaxTextChunkBytes - output.Length) throw new InvalidDataException("PNG text output exceeds the chunk limit.");
            AccountText(count, ref totalTextBytes);
            output.Write(buffer, 0, count);
        }
        return encoding.GetString(output.ToArray());
    }

    private static byte[] ReadData(Stream stream, int length, CancellationToken cancellationToken)
    {
        var data = new byte[length];
        for (var offset = 0; offset < length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = stream.Read(data, offset, Math.Min(64 * 1024, length - offset));
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        return data;
    }

    private static void AccountText(long bytes, ref long total)
    {
        if (bytes > MaxTotalTextBytes - total) throw new InvalidDataException("PNG total text exceeds the limit.");
        total += bytes;
    }
}
